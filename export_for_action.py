#!/usr/bin/env python
"""导出 BiliCookieLauncher 本地 Cookie 为 GitHub Action 保活项目的配置文件。

用法（在 BiliCookieLauncher 的虚拟环境中执行）::

    python export_for_action.py

流程：
1. 读取本地 config.json（复用 core.config），检查首次运行状态与 encryption_salt；
2. 通过 getpass 安全读取解锁密码（不回显）；
3. 复用 core.crypto.verify_password 校验 Argon2 哈希，密码错误直接退出；
4. 复用 core.crypto.derive_key 由「密码 + encryption_salt」派生本地 Fernet 密钥
   （该密钥即本地 cookies.enc 的加密密钥，base64-urlsafe 字符串）；
5. 复用 core.storage.CookieStore 解密 cookies.enc，得到 {site: [cookies]}；
6. 复用 core.exporter 对每个站点用同一 Fernet 密钥加密「该站点 Cookie 数组的
   JSON 文本」，生成 fernet_token 并写出 action_config_output.json；
7. 控制台打印 Fernet 密钥，提示存入 GitHub Secret（建议名称 COOKIE_FERNET_KEY）。

说明：
- 本 CLI 脚本与程序内「主菜单 3 导出 Cookie」共用 core.exporter 的同一套逻辑；
- 程序内导出另有二重导出密码门禁（core/ui.action_export），本脚本不要求
  二重密码，仅为命令行便捷入口，请自行妥善保管运行环境。

安全约束：
- 明文 Cookie 只存在于本脚本运行时的内存中，绝不打印、绝不写入输出文件；
- encryption_salt 仅用于本地导出过程中的密钥派生，禁止写入输出文件；
- 修改 BiliCookieLauncher 解锁密码后，必须重新运行本脚本，并同步更新
  GitHub Secret 与仓库中的 action_config_output.json。
"""

from __future__ import annotations

import getpass
import sys

from core import crypto, config
from core.exporter import GITHUB_SECRET_NAME, OUTPUT_FILE_NAME, ExporterError, export_action_config
from core.storage import CookieStore, StorageError


def _ensure_utf8_stdio() -> None:
    """输出被重定向到文件或管道时，强制使用 UTF-8，避免中文报错。"""
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            continue
        try:
            if not stream.isatty():
                stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def _print_error(message: str) -> None:
    """统一的错误提示文案（不暴露原始堆栈）。"""
    print("[错误] {}".format(message))


def _read_password() -> str:
    """安全读取解锁密码（不回显）。

    Returns:
        用户输入的密码；用户取消（Ctrl+C / EOF）时返回空字符串。
    """
    try:
        return getpass.getpass("请输入 BiliCookieLauncher 解锁密码: ")
    except KeyboardInterrupt:
        print("\n已取消导出。")
        return ""
    except EOFError:
        print("\n输入流中断，已取消导出。")
        return ""


def main() -> int:
    """导出主流程。

    Returns:
        进程退出码，0 表示成功。
    """
    _ensure_utf8_stdio()

    # 1. 路径定位（源码运行 = 项目根，与 main.py 一致）
    app_root = config.get_app_root()
    config_path = config.get_config_path()
    cookie_path = config.get_cookie_file_path()

    # 2. 读取本地配置
    try:
        conf = config.load_config()
    except config.ConfigError as exc:
        _print_error(str(exc))
        return 1

    # 3. 检查必要字段
    if config.is_first_run(conf):
        _print_error("尚未设置解锁密码，请先运行 BiliCookieLauncher（main.py）完成首次设置。")
        return 1
    salt = str(conf.get("encryption_salt") or "")
    if not salt:
        _print_error("配置文件缺少 encryption_salt，无法派生密钥。请检查 config.json。")
        return 1

    # 4. cookies.enc 存在性检查
    if not cookie_path.exists():
        _print_error(
            "cookies.enc 不存在（{}）。请先在 BiliCookieLauncher 中添加网站 Cookie 后再导出。".format(
                cookie_path
            )
        )
        return 1

    # 5. 密码校验（Argon2，密码错误直接退出，不重试）
    password = _read_password()
    if not password:
        return 1
    if not crypto.verify_password(str(conf.get("password_hash") or ""), password):
        _print_error("密码错误，导出已终止。")
        return 1

    # 6. 派生本地 Fernet 密钥并解密 cookies.enc
    try:
        key = crypto.derive_key(password, salt)
    except crypto.CryptoError as exc:
        _print_error(str(exc))
        return 1

    store = CookieStore(cookie_path, key)
    try:
        data = store.load()
    except crypto.DecryptError:
        _print_error("密码错误或数据损坏，导出已终止。")
        return 1
    except StorageError as exc:
        _print_error("Cookie 数据文件异常: {}".format(exc))
        return 1

    # 7. 组装并写出输出文件（明文 Cookie 仅存在于内存）
    out_path = app_root / OUTPUT_FILE_NAME
    try:
        export_action_config(
            data,
            key,
            out_path,
            on_skip=lambda site: print("跳过空 Cookie 列表的站点: {}".format(site)),
        )
    except ExporterError as exc:
        _print_error(str(exc))
        return 1

    # 8. 控制台提示（密钥 / 文件 / 改密码联动提醒）
    print()
    print("=" * 56)
    print("导出完成。")
    print()
    print("【GitHub Secret】以下 Fernet 密钥请保存到 GitHub Secret，")
    print("Secret 名称建议为：{}".format(GITHUB_SECRET_NAME))
    print("密钥（base64-urlsafe）:")
    print(key.decode("ascii"))
    print()
    print("【输出文件】已生成：{}".format(out_path))
    print("  该文件仅包含 Fernet 密文，不含明文 Cookie / encryption_salt / 密码，")
    print("  可以提交到 GitHub 仓库。")
    print()
    print("【重要警告】如果之后修改了 BiliCookieLauncher 解锁密码，必须：")
    print("  1. 重新运行本脚本（python export_for_action.py）")
    print("  2. 同步更新 GitHub Secret（{}）".format(GITHUB_SECRET_NAME))
    print("  3. 同步更新仓库中的 action_config_output.json")
    print("=" * 56)
    return 0


if __name__ == "__main__":
    sys.exit(main())
