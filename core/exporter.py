"""导出 Cookie 为 GitHub Action 保活配置文件（核心逻辑，UI 菜单与 CLI 共用）。

职责边界：
- 本模块只负责「组装输出结构 + 写文件」，不含密码读取、密码校验、
  二重密码门禁、cookies.enc 解密（这些由调用方完成并传入数据）；
- 复用 core.crypto 的 Fernet 实现与密钥（由调用方传入本地派生密钥）；
- 明文 Cookie 只存在于内存参数中，绝不打印、绝不写入输出文件；
- 输出文件 action_config_output.json 仅包含 Fernet 密文与站点域名，
  不含明文 Cookie、不含 encryption_salt、不含用户密码。
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from . import crypto

# 建议的 GitHub Secret 名称
GITHUB_SECRET_NAME = "COOKIE_FERNET_KEY"

# 输出文件名（生成在程序根目录）
OUTPUT_FILE_NAME = "action_config_output.json"

# global 固定请求参数（GitHub Action 保活侧使用，与本地配置无关）
GLOBAL_CONFIG: Dict[str, Any] = {
    "userAgent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    ),
    "timeout": 30000,
    "loginInvalidKeyword": "请登录",
}


class ExporterError(Exception):
    """导出失败（空数据 / 加密失败 / 写入失败等）。"""


def build_action_output(
    data: Dict[str, List[Dict[str, Any]]],
    key: bytes,
    on_skip: Optional[Callable[[str], None]] = None,
) -> Dict[str, Any]:
    """把解密后的 Cookie 数据组装为 Action 保活配置结构。

    Args:
        data: cookies.enc 解密结果（site -> Cookie 列表）。
        key: 本地 Fernet 密钥（由解锁密码 + encryption_salt 派生）。
        on_skip: 回调，收到被跳过的空站点域名（可选，用于调用方提示）。

    Returns:
        {"global": {...}, "cookies": [{"site": ..., "fernet_token": ...}, ...]}。

    Raises:
        ExporterError: 没有可导出的站点数据（data 为空或全部站点为空），
            或站点 Cookie 序列化/加密失败。
    """
    if not data:
        raise ExporterError("cookies.enc 中没有可导出的站点数据，未生成输出文件。")

    try:
        fernet_class, _ = crypto._import_fernet()
    except crypto.MissingDependencyError as exc:
        raise ExporterError(str(exc)) from exc

    cookies_out: List[Dict[str, Any]] = []
    for site, site_cookies in data.items():
        cleaned = [item for item in site_cookies if isinstance(item, dict)]
        if not cleaned:
            if on_skip is not None:
                on_skip(site)
            continue
        try:
            payload = json.dumps(cleaned, ensure_ascii=False).encode("utf-8")
        except (TypeError, ValueError) as exc:
            raise ExporterError("站点 {} 的 Cookie 无法序列化为 JSON: {}".format(site, exc)) from exc
        try:
            token = fernet_class(key).encrypt(payload).decode("ascii")
        except Exception as exc:
            raise ExporterError("站点 {} 的 Cookie 加密失败: {}".format(site, exc)) from exc
        cookies_out.append({"site": site, "fernet_token": token})

    if not cookies_out:
        raise ExporterError("没有可导出的站点 Cookie（全部站点均为空），未生成输出文件。")

    return {"global": dict(GLOBAL_CONFIG), "cookies": cookies_out}


def write_action_output(output: Dict[str, Any], out_path: Path) -> Path:
    """把输出结构写入 JSON 文件（先写临时文件再替换，避免写坏）。

    Args:
        output: build_action_output 的返回结构。
        out_path: 目标文件路径（已存在时直接覆盖）。

    Returns:
        写入后的文件路径。

    Raises:
        ExporterError: 写入失败。
    """
    tmp_path = out_path.with_name(out_path.name + ".tmp")
    try:
        with tmp_path.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(output, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        os.replace(str(tmp_path), str(out_path))
    except OSError as exc:
        raise ExporterError("无法写入输出文件 {}: {}".format(out_path, exc)) from exc
    return out_path


def export_action_config(
    data: Dict[str, List[Dict[str, Any]]],
    key: bytes,
    out_path: Path,
    on_skip: Optional[Callable[[str], None]] = None,
) -> Path:
    """组装并写出 Action 保活配置文件。

    Args:
        data: cookies.enc 解密结果（site -> Cookie 列表）。
        key: 本地 Fernet 密钥。
        out_path: 输出文件路径（程序根目录下的 action_config_output.json）。
        on_skip: 回调，收到被跳过的空站点域名（可选）。

    Returns:
        输出文件路径。

    Raises:
        ExporterError: 空数据 / 加密失败 / 写入失败。
    """
    output = build_action_output(data, key, on_skip=on_skip)
    return write_action_output(output, out_path)
