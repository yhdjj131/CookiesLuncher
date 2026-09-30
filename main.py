#!/usr/bin/env python
"""BiliCookieLauncher 通用 Cookie 启动器 - 程序入口。

启动流程：
1. 读取 config.json（不存在则生成默认配置）
2. 清理过期日志
3. 首次运行则引导设置密码，否则校验密码
4. 密码验证成功后进入主菜单，由用户手动选择运行模式或修改模式
5. 记录日志并退出
"""

from __future__ import annotations

import sys
from typing import Any, Dict, Optional

from core import crypto, ui
from core.browser import BrowserError, setup_playwright_env
from core.config import (
    ConfigError,
    generate_salt,
    get_config_path,
    get_cookie_file_path,
    get_log_dir,
    is_first_run,
    load_config,
    save_config,
)
from core.logger import cleanup_old_logs, setup_logger
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


def ui_error_text(message: str) -> str:
    """统一的错误提示文案。"""
    return "[错误] {}".format(message)


def _setup_first_run(config: Dict[str, Any], logger: Any) -> bytes:
    """首次运行：引导设置密码，生成盐值并创建空的 cookies.enc。

    Args:
        config: 配置字典（会被就地修改并保存）。
        logger: 日志记录器。

    Returns:
        由新密码派生的 Fernet 密钥。
    """
    print("\n{} 首次运行，请设置访问密码 {}".format(ui.SEPARATOR, ui.SEPARATOR))
    print("提示：密码用于登录本程序，同时也用于加密 Cookie 文件，请务必牢记。")

    password = ui.prompt_new_password()
    salt = generate_salt()

    key = crypto.derive_key(password, salt)
    config["password_hash"] = crypto.hash_password(password)
    config["encryption_salt"] = salt
    save_config(config)

    store = CookieStore(get_cookie_file_path(), key)
    created = store.ensure_file()

    logger.info("首次运行：密码设置完成，配置文件已生成")
    if created:
        logger.info("首次运行：已生成空的 cookies.enc")
    print("设置完成，配置文件：{}".format(get_config_path()))
    return key


def _unlock(config: Dict[str, Any], logger: Any) -> Optional[bytes]:
    """循环校验密码并派生密钥，直到成功或用户中断。

    Args:
        config: 配置字典。
        logger: 日志记录器。

    Returns:
        派生出的 Fernet 密钥；用户中断时返回 None。
    """
    salt = str(config.get("encryption_salt") or "")
    if not salt:
        print(ui_error_text("配置文件缺少 encryption_salt，无法解锁。请删除 config.json 后重新运行。"))
        logger.error("配置文件缺少 encryption_salt")
        return None

    while True:
        try:
            password = ui.read_password("请输入密码: ")
        except KeyboardInterrupt:
            print("\n已取消。")
            logger.info("用户中断密码输入")
            return None

        if not password:
            print(ui_error_text("密码不能为空，请重新输入。"))
            continue

        if not crypto.verify_password(config.get("password_hash", ""), password):
            print(ui_error_text("密码错误，请重试。"))
            logger.warning("密码验证失败")
            continue

        key = crypto.derive_key(password, salt)

        # 用密钥试解密 cookies.enc，确保密钥与数据一致（同时能发现数据损坏）
        store = CookieStore(get_cookie_file_path(), key)
        try:
            store.load()
        except crypto.DecryptError:
            print(ui_error_text("密码错误或数据损坏，请重试。"))
            logger.warning("解密 cookies.enc 失败：密码错误或数据损坏")
            continue
        except StorageError as exc:
            print(ui_error_text("Cookie 数据文件异常: {}".format(exc)))
            logger.error("cookies.enc 结构异常: %s", exc)
            return None

        logger.info("密码验证成功")
        return key


def prepare_browser_env(logger: Any) -> None:
    """尽早设置 PLAYWRIGHT_BROWSERS_PATH。

    必须在任何 ``import playwright`` 之前执行。找不到浏览器时只记录警告、
    不阻塞启动，这样打包不完整时用户仍可用修改模式管理 Cookie；
    真正启动浏览器时会再次检查并给出明确错误。

    Args:
        logger: 日志记录器。
    """
    try:
        browser_root = setup_playwright_env()
        logger.debug("浏览器目录: %s", browser_root)
    except BrowserError as exc:
        logger.warning("浏览器环境准备失败: %s", exc)


def main() -> int:
    """程序主入口。

    Returns:
        进程退出码，0 表示正常退出。
    """
    _ensure_utf8_stdio()

    # 1. 读取配置
    try:
        config = load_config()
    except ConfigError as exc:
        print(ui_error_text(str(exc)))
        return 1

    # 2. 初始化日志并清理过期日志
    logger = setup_logger(get_log_dir(), config.get("log_level", "INFO"))
    logger.info("程序启动")

    try:
        cleanup_old_logs(get_log_dir(), int(config.get("log_retention_days", 30)), logger)
    except Exception as exc:  # 清理失败不影响启动
        logger.warning("清理过期日志失败: %s", exc)

    # 尽早设置浏览器目录（在任何 import playwright 之前）
    prepare_browser_env(logger)

    try:
        # 3. 密码设置或校验
        if is_first_run(config):
            key = _setup_first_run(config, logger)
        else:
            key = _unlock(config, logger)
            if key is None:
                logger.info("程序退出")
                return 0

        context = ui.AppContext(config, key, logger)

        # 4. 密码验证成功后进入主菜单（不再依赖 config.json 的 modify_mode 标记）
        ui.main_menu(context)

    except KeyboardInterrupt:
        # Ctrl+C 优雅退出；浏览器临时目录由 browser.run_session 的 finally 负责清理
        print("\n已中断，程序退出。")
        logger.info("用户中断（Ctrl+C），程序退出")
        return 0
    except crypto.MissingDependencyError as exc:
        print(ui_error_text(str(exc)))
        logger.error("缺少依赖: %s", exc)
        return 1
    except Exception as exc:
        logger.exception("程序运行出现未处理异常")
        print(ui_error_text("程序异常: {}".format(exc)))
        print("详细信息已写入日志：{}".format(get_log_dir()))
        return 1

    logger.info("程序退出")
    return 0


if __name__ == "__main__":
    sys.exit(main())
