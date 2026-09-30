"""配置文件的读写、默认值合并与基础校验。

本模块同时负责统一解析程序的各类路径（配置文件、Cookie 存储、日志目录），
打包（PyInstaller）与源码运行两种场景都能正确定位。
"""

from __future__ import annotations

import base64
import json
import os
import re
import secrets
import sys
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

# 默认配置，字段含义见 README
DEFAULT_CONFIG: Dict[str, Any] = {
    "password_hash": "",
    "encryption_salt": "",
    "second_password_hash": "",
    "log_level": "INFO",
    "log_retention_days": 30,
    "browser": {
        "headless": False,
        "window_size": "maximized",
    },
}

# 允许的日志级别
VALID_LOG_LEVELS = ("DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL")

# 盐值长度（字节）
SALT_BYTES = 16

# 形如 1280x720 / 1280*720 / 1280,720 的窗口尺寸
_WINDOW_SIZE_RE = re.compile(r"^\s*(\d{3,5})\s*[xX*,]\s*(\d{3,5})\s*$")


class ConfigError(Exception):
    """配置文件读写或校验失败。"""


def get_app_root() -> Path:
    """返回程序根目录。

    打包后为可执行文件所在目录，源码运行时为项目根目录
    （即 core 包的上一级目录）。
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def get_config_path() -> Path:
    """返回 config.json 的绝对路径。"""
    return get_app_root() / "config.json"


def get_cookie_file_path() -> Path:
    """返回 cookies.enc 的绝对路径。"""
    return get_app_root() / "cookies.enc"


def get_log_dir() -> Path:
    """返回 logs 目录的绝对路径。"""
    return get_app_root() / "logs"


def generate_salt() -> str:
    """生成 base64 编码的随机盐值（16 字节）。"""
    return base64.b64encode(secrets.token_bytes(SALT_BYTES)).decode("ascii")


def _deep_merge(defaults: Dict[str, Any], data: Dict[str, Any]) -> Dict[str, Any]:
    """把用户配置深度合并到默认配置之上，缺失字段自动补齐。"""
    merged: Dict[str, Any] = dict(defaults)
    for key, value in data.items():
        current = merged.get(key)
        if isinstance(current, dict) and isinstance(value, dict):
            merged[key] = _deep_merge(current, value)
        else:
            merged[key] = value
    return merged


def normalize_config(data: Dict[str, Any]) -> Dict[str, Any]:
    """校验并规范化配置内容，非法值回退为默认值。

    Args:
        data: 从 config.json 读出的原始字典。

    Returns:
        字段完整、类型正确的配置字典。
    """
    if not isinstance(data, dict):
        raise ConfigError("配置文件内容必须是 JSON 对象")

    config = _deep_merge(DEFAULT_CONFIG, data)

    # 兼容旧版 config.json：modify_mode 标记已废弃（不再通过配置文件进入修改模式），
    # 直接移除该字段，避免遗留废弃判断逻辑。
    config.pop("modify_mode", None)

    config["password_hash"] = str(config.get("password_hash") or "")
    config["encryption_salt"] = str(config.get("encryption_salt") or "")
    config["second_password_hash"] = str(config.get("second_password_hash") or "")

    level = str(config.get("log_level") or "INFO").strip().upper()
    config["log_level"] = level if level in VALID_LOG_LEVELS else "INFO"

    try:
        retention = int(config.get("log_retention_days", 30))
    except (TypeError, ValueError):
        retention = 30
    config["log_retention_days"] = retention if retention > 0 else 30

    browser = config.get("browser")
    if not isinstance(browser, dict):
        browser = {}
    window_size = browser.get("window_size", "maximized")
    window_size = str(window_size).strip() if window_size else "maximized"
    config["browser"] = {
        "headless": bool(browser.get("headless", False)),
        "window_size": window_size or "maximized",
    }
    return config


def load_config() -> Dict[str, Any]:
    """读取 config.json，不存在时自动生成默认配置。

    Returns:
        规范化后的配置字典。

    Raises:
        ConfigError: 文件存在但无法读取或 JSON 损坏。
    """
    path = get_config_path()
    if not path.exists():
        config = normalize_config({})
        save_config(config)
        return config

    try:
        # utf-8-sig 兼容记事本等编辑器写入的 BOM
        raw = path.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ConfigError("无法读取配置文件 {}: {}".format(path, exc))
    except UnicodeDecodeError as exc:
        raise ConfigError("配置文件 {} 不是 UTF-8 编码: {}".format(path, exc))

    if not raw.strip():
        # 空文件按默认配置处理，避免用户误清空后无法启动
        config = normalize_config({})
        save_config(config)
        return config

    try:
        data = json.loads(raw)
    except ValueError as exc:
        raise ConfigError(
            "配置文件 {} 不是合法的 JSON（{}），请修复或删除后重新运行".format(path, exc)
        )

    return normalize_config(data)


def save_config(config: Dict[str, Any]) -> None:
    """把配置写回 config.json（先写临时文件再替换，避免写坏原文件）。

    Args:
        config: 待保存的配置字典。

    Raises:
        ConfigError: 写入失败。
    """
    path = get_config_path()
    tmp_path = path.with_name(path.name + ".tmp")
    try:
        with tmp_path.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(config, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        os.replace(str(tmp_path), str(path))
    except OSError as exc:
        raise ConfigError("无法写入配置文件 {}: {}".format(path, exc))


def is_first_run(config: Dict[str, Any]) -> bool:
    """判断是否首次运行（尚未设置密码）。"""
    return not str(config.get("password_hash") or "").strip()


def parse_window_size(value: Any) -> Optional[Tuple[int, int]]:
    """解析 browser.window_size 配置。

    Args:
        value: 配置值，例如 "maximized" 或 "1280x720"。

    Returns:
        (宽, 高) 元组；若为 maximized 或无法识别则返回 None。
    """
    if not value:
        return None
    text = str(value).strip().lower()
    if text in ("maximized", "max", "fullscreen", "default"):
        return None
    match = _WINDOW_SIZE_RE.match(text)
    if not match:
        return None
    width, height = int(match.group(1)), int(match.group(2))
    if width <= 0 or height <= 0:
        return None
    return width, height


def is_maximized(value: Any) -> bool:
    """判断窗口配置是否为「最大化」模式。"""
    return parse_window_size(value) is None
