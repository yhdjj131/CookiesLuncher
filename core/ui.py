"""控制台交互：菜单、输入校验、各功能操作。

约定：
- 全部中文提示，不使用 emoji 与复杂框线；
- 标题用 ``==========`` 包裹；
- 选项范围用 ``[1-5]`` 形式提示；
- 输入非法时提示重新输入，不崩溃；
- Ctrl+C 交由上层统一处理，保证优雅退出。
"""

from __future__ import annotations

import getpass
import sys
import traceback
from pathlib import Path
from typing import Any, Dict, List, Optional

from . import cookie_capture, crypto, exporter
from .browser import BrowserError, run_session
from .config import generate_salt, get_config_path, get_cookie_file_path, save_config
from .cookie_parser import (
    CookieParseError,
    normalize_site,
    parse_cookie_text_detailed,
    summarize_cookies,
)
from .storage import CookieStore, StorageError

# 密码最小长度
MIN_PASSWORD_LENGTH = 6

# 标题分隔线
SEPARATOR = "=========="


class AppContext:
    """运行期上下文：配置、加密密钥与日志记录器。

    Attributes:
        config: 配置字典（与 config.json 同步）。
        key: 当前有效的 Fernet 密钥。
        logger: 日志记录器。
    """

    def __init__(self, config: Dict[str, Any], key: bytes, logger: Any) -> None:
        """初始化上下文。

        Args:
            config: 配置字典。
            key: 当前密码派生出的密钥。
            logger: 日志记录器。
        """
        self.config = config
        self.key = key
        self.logger = logger
        self.cookie_path: Path = get_cookie_file_path()
        self.config_path: Path = get_config_path()

    @property
    def store(self) -> CookieStore:
        """按当前密钥构造 Cookie 存储对象。"""
        return CookieStore(self.cookie_path, self.key)

    def update_key(self, new_key: bytes) -> None:
        """修改密码后更新密钥。"""
        self.key = new_key


# --------------------------------------------------------------------- 基础输出


def print_title(title: str) -> None:
    """打印标题行。"""
    print("\n{} {} {}".format(SEPARATOR, title, SEPARATOR))


def print_error(message: str) -> None:
    """打印错误提示。"""
    print("[错误] {}".format(message))


def print_info(message: str) -> None:
    """打印普通提示。"""
    print(message)


# --------------------------------------------------------------------- 输入封装


def read_text(prompt_text: str, allow_empty: bool = False) -> str:
    """读取一行文本输入。

    Args:
        prompt_text: 提示语。
        allow_empty: 是否允许空输入。

    Returns:
        去掉首尾空格后的用户输入。

    Raises:
        KeyboardInterrupt: 输入流结束（EOF）或用户按下 Ctrl+C。
    """
    while True:
        try:
            value = input(prompt_text)
        except EOFError:
            raise KeyboardInterrupt
        value = value.strip()
        if value or allow_empty:
            return value
        print_error("输入不能为空，请重新输入。")


def read_index(prompt_text: str, minimum: int, maximum: int) -> int:
    """读取一个范围内的整数选项。

    Args:
        prompt_text: 提示语。
        minimum: 最小可选值。
        maximum: 最大可选值。

    Returns:
        用户选择的整数。

    Raises:
        KeyboardInterrupt: 输入流结束或用户按下 Ctrl+C。
    """
    while True:
        try:
            raw = input(prompt_text)
        except EOFError:
            raise KeyboardInterrupt
        raw = raw.strip()
        if not raw:
            print_error("请输入 {}-{} 之间的数字。".format(minimum, maximum))
            continue
        try:
            value = int(raw)
        except ValueError:
            print_error("请输入 {}-{} 之间的数字。".format(minimum, maximum))
            continue
        if value < minimum or value > maximum:
            print_error("请输入 {}-{} 之间的数字。".format(minimum, maximum))
            continue
        return value


def confirm(prompt_text: str, default: bool = False) -> bool:
    """读取是/否确认。

    Args:
        prompt_text: 提示语。
        default: 直接回车时的默认结果。

    Returns:
        用户确认返回 True。
    """
    suffix = "[Y/n]" if default else "[y/N]"
    while True:
        try:
            raw = input("{} {}: ".format(prompt_text, suffix))
        except EOFError:
            raise KeyboardInterrupt
        raw = raw.strip().lower()
        if not raw:
            return default
        if raw in ("y", "yes", "是"):
            return True
        if raw in ("n", "no", "否"):
            return False
        print_error("请输入 y 或 n。")


def read_password(prompt_text: str = "请输入密码: ") -> str:
    """读取密码输入（终端下不回显）。

    Args:
        prompt_text: 提示语。

    Returns:
        用户输入的密码。

    Raises:
        KeyboardInterrupt: 输入流结束或用户按下 Ctrl+C。
    """
    try:
        if sys.stdin is not None and sys.stdin.isatty():
            return getpass.getpass(prompt_text)
        return input(prompt_text)
    except EOFError:
        raise KeyboardInterrupt


def read_multiline(prompt_text: str) -> str:
    """读取多行粘贴内容，遇到空行结束。

    Args:
        prompt_text: 提示语。

    Returns:
        由换行符连接的多行文本。

    Raises:
        KeyboardInterrupt: 输入流结束或用户按下 Ctrl+C。
    """
    print(prompt_text)
    lines: List[str] = []
    while True:
        try:
            line = input()
        except EOFError:
            raise KeyboardInterrupt
        if line.strip() == "":
            break
        lines.append(line)
    return "\n".join(lines)


def prompt_new_password() -> str:
    """引导用户两次输入新密码并做基本校验。

    Returns:
        通过校验的新密码。

    Raises:
        KeyboardInterrupt: 用户中断输入。
    """
    while True:
        first = read_password("请输入新密码（至少 {} 位）: ".format(MIN_PASSWORD_LENGTH))
        if len(first) < MIN_PASSWORD_LENGTH:
            print_error("密码长度不能少于 {} 位。".format(MIN_PASSWORD_LENGTH))
            continue
        if first != first.strip():
            print_error("密码首尾不能包含空格。")
            continue
        second = read_password("请再次输入新密码: ")
        if first != second:
            print_error("两次输入的密码不一致，请重新输入。")
            continue
        return first


# --------------------------------------------------------------------- 业务操作


def action_add_site(context: AppContext) -> None:
    """添加或替换一个网站的 Cookie（粘贴文本 / 浏览器手动登录采集两种来源）。

    Args:
        context: 运行期上下文。
    """
    print_title("添加或替换网站 Cookie")
    print("  1. 粘贴 JSON/文本")
    print("  2. 浏览器手动登录并采集 Cookie")
    print("  0. 取消")
    source = read_index("请选择 Cookie 来源 [0-2]: ", 0, 2)
    if source == 0:
        print_info("已取消。")
        return
    if source == 2:
        _action_capture(context)
    else:
        _action_paste(context)


def _action_paste(context: AppContext) -> None:
    """粘贴方式：输入域名 -> 粘贴 Cookie 文本 -> 解析 -> 覆盖判断 -> 写入。"""
    print_title("粘贴网站 Cookie")

    while True:
        raw_site = read_text("请输入网站域名（如 bilibili.com，直接回车取消）: ", allow_empty=True)
        if not raw_site:
            print_info("已取消。")
            return
        try:
            site = normalize_site(raw_site)
            break
        except CookieParseError as exc:
            print_error(str(exc))

    raw_cookie = read_multiline(
        "请粘贴 Cookie（支持分号格式、标准 JSON、Cookie-Editor 导出的 JSON，输入空行结束）:"
    )
    if not raw_cookie.strip():
        print_info("未输入任何内容，已取消。")
        return

    try:
        cookies, warnings = parse_cookie_text_detailed(raw_cookie, site)
    except CookieParseError as exc:
        print_error(str(exc))
        context.logger.warning("解析 Cookie 失败: %s", exc)
        return

    for warning in warnings:
        print_info("[提示] {}".format(warning))
    print_info("解析成功：{} 条 Cookie（{}）".format(len(cookies), summarize_cookies(cookies)))

    try:
        store = context.store
        # 仅在 UI 层做存在性判断：已存在则提示覆盖并请求确认，不存在则提示全新添加
        exists = bool(store.get(site))
        if exists:
            print_info("该网站已存在 Cookie，本次操作将直接替换覆盖。")
            if not confirm("网站 {} 已存在，是否替换覆盖".format(site), default=False):
                print_info("已取消，未做修改。")
                return
        else:
            print_info("全新添加网站 Cookie。")
        overwritten = store.upsert(site, cookies)
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取现有 Cookie 数据。")
        context.logger.error("写入 Cookie 失败：cookies.enc 解密失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("写入 Cookie 失败: %s", exc)
        return

    if overwritten:
        print_info("已覆盖网站 {}，共 {} 条 Cookie。".format(site, len(cookies)))
        context.logger.info("已覆盖网站 Cookie %s（%d 条）", site, len(cookies))
    else:
        print_info("已添加网站 {}，共 {} 条 Cookie。".format(site, len(cookies)))
        context.logger.info("已添加网站 Cookie %s（%d 条）", site, len(cookies))


def _action_capture(context: AppContext) -> None:
    """浏览器采集方式：输入登录 URL -> 自动识别域名 -> 启动可见浏览器手动登录
    -> 回车确认 -> 读取 Cookie -> 过滤 -> 覆盖确认 -> 写入 cookies.enc。

    Args:
        context: 运行期上下文。
    """
    print_title("浏览器手动登录并采集 Cookie")
    print_info("[安全提示] Cookie 属于身份凭证，采集后将加密保存在本机 cookies.enc，"
               "请妥善保管密码；请勿向他人泄露采集到的 Cookie 内容。")

    while True:
        raw_url = read_text("请输入登录 URL（如 https://www.bilibili.com/xxx，直接回车取消）: ",
                            allow_empty=True)
        if not raw_url:
            print_info("已取消。")
            return
        try:
            site = normalize_site(raw_url)
            break
        except CookieParseError as exc:
            print_error(str(exc))

    # 自动识别域名，允许用户手动修改
    modified = read_text("识别到域名：{}，回车确认或输入新域名: ".format(site), allow_empty=True)
    if modified.strip():
        try:
            site = normalize_site(modified)
        except CookieParseError as exc:
            print_error(str(exc))
            return

    try:
        exists = bool(context.store.get(site))
    except (crypto.DecryptError, StorageError):
        exists = False
    if exists:
        print_info("该网站已存在 Cookie，采集完成后将提示替换覆盖。")
    else:
        print_info("该网站暂无 Cookie，本次为全新添加。")

    try:
        cookies = cookie_capture.capture_cookies(raw_url, context.config, context.logger)
    except (cookie_capture.CookieCaptureError, BrowserError) as exc:
        print_error(str(exc))
        context.logger.error("采集 Cookie 失败: %s", exc)
        return
    except KeyboardInterrupt:
        print("\n已取消采集。")
        context.logger.info("用户中断采集")
        return
    except Exception as exc:
        print_error("采集过程出现异常: {}".format(exc))
        context.logger.error("采集 Cookie 异常: %s", exc)
        return

    if not cookies:
        print_error("未采集到任何有效 Cookie，已拒绝保存。")
        context.logger.warning("采集返回空 Cookie 列表，拒绝保存")
        return

    print_info("采集到 {} 条 Cookie（{}）".format(len(cookies), summarize_cookies(cookies)))

    try:
        store = context.store
        exists = bool(store.get(site))
        if exists:
            print_info("该网站已存在 Cookie，本次操作将直接替换覆盖。")
            if not confirm("网站 {} 已存在，是否替换覆盖".format(site), default=False):
                print_info("已取消，未做修改。")
                return
        else:
            print_info("全新添加网站 Cookie。")
        overwritten = store.upsert(site, cookies)
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取现有 Cookie 数据。")
        context.logger.error("写入 Cookie 失败：cookies.enc 解密失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("写入 Cookie 失败: %s", exc)
        return

    if overwritten:
        print_info("已覆盖网站 {}，共 {} 条 Cookie。".format(site, len(cookies)))
        context.logger.info("已覆盖网站 Cookie %s（%d 条）", site, len(cookies))
    else:
        print_info("已添加网站 {}，共 {} 条 Cookie。".format(site, len(cookies)))
        context.logger.info("已添加网站 Cookie %s（%d 条）", site, len(cookies))


def action_list_sites(context: AppContext) -> None:
    """查看已配置的网站及 Cookie 数量（不显示 Cookie 值）。

    Args:
        context: 运行期上下文。
    """
    print_title("已配置网站")
    try:
        sites = context.store.list_sites()
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取 Cookie 数据。")
        context.logger.error("读取网站列表失败：cookies.enc 解密失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("读取网站列表失败: %s", exc)
        return

    if not sites:
        print_info("暂无已配置的网站。")
        return

    for index, (site, count) in enumerate(sites, start=1):
        print("  {}. {}（{} 个 Cookie）".format(index, site, count))
    print_info("共 {} 个网站。".format(len(sites)))


def action_delete_site(context: AppContext) -> None:
    """删除指定网站的 Cookie。

    Args:
        context: 运行期上下文。
    """
    print_title("删除网站 Cookie")
    try:
        sites = context.store.list_sites()
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取 Cookie 数据。")
        context.logger.error("删除网站失败：cookies.enc 解密失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("删除网站失败: %s", exc)
        return

    if not sites:
        print_info("暂无已配置的网站，无需删除。")
        return

    for index, (site, count) in enumerate(sites, start=1):
        print("  {}. {}（{} 个 Cookie）".format(index, site, count))
    print("  0. 取消")

    choice = read_index("请选择要删除的网站 [0-{}]: ".format(len(sites)), 0, len(sites))
    if choice == 0:
        print_info("已取消。")
        return

    site = sites[choice - 1][0]
    if not confirm("确定要删除 {} 的全部 Cookie 吗，此操作不可恢复".format(site), default=False):
        print_info("已取消。")
        return

    try:
        removed = context.store.delete(site)
    except (crypto.DecryptError, StorageError) as exc:
        print_error("删除失败: {}".format(exc))
        context.logger.error("删除网站 %s 失败: %s", site, exc)
        return

    if removed:
        print_info("已删除 {}。".format(site))
        context.logger.info("已删除网站 Cookie %s", site)
    else:
        print_error("未找到网站 {}，可能已被删除。".format(site))


def action_change_password(context: AppContext) -> None:
    """修改访问密码，并用新密钥重新加密 cookies.enc。

    Args:
        context: 运行期上下文。
    """
    print_title("修改密码")

    old_password = read_password("请输入当前密码: ")
    if not crypto.verify_password(context.config.get("password_hash", ""), old_password):
        print_error("当前密码错误。")
        context.logger.warning("修改密码失败：旧密码错误")
        return

    new_password = prompt_new_password()
    if new_password == old_password:
        print_info("新密码与旧密码相同，无需修改。")
        return

    # 用旧密钥读出数据
    try:
        data = context.store.load()
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取现有 Cookie 数据。")
        context.logger.error("修改密码失败：用旧密钥解密 cookies.enc 失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("修改密码失败: %s", exc)
        return

    new_salt = generate_salt()
    new_key = crypto.derive_key(new_password, new_salt)
    new_hash = crypto.hash_password(new_password)

    backup: Optional[bytes] = None
    if context.cookie_path.exists():
        try:
            backup = context.cookie_path.read_bytes()
        except OSError:
            backup = None

    # 先用新密钥重写 cookies.enc，再更新 config.json
    try:
        CookieStore(context.cookie_path, new_key).save(data)
    except StorageError as exc:
        print_error("重新加密 Cookie 数据失败: {}".format(exc))
        context.logger.error("修改密码失败: %s", exc)
        return

    previous_hash = context.config.get("password_hash", "")
    previous_salt = context.config.get("encryption_salt", "")
    context.config["password_hash"] = new_hash
    context.config["encryption_salt"] = new_salt
    try:
        save_config(context.config)
    except Exception as exc:
        # 回滚：恢复旧的 cookies.enc 与配置，避免新密码无法解密旧数据
        context.config["password_hash"] = previous_hash
        context.config["encryption_salt"] = previous_salt
        if backup is not None:
            try:
                context.cookie_path.write_bytes(backup)
            except OSError:
                context.logger.error("回滚 cookies.enc 失败，请手动用备份恢复")
        print_error("保存配置失败，已回滚: {}".format(exc))
        context.logger.error("修改密码失败并已回滚: %s", exc)
        return

    context.update_key(new_key)
    print_info("密码修改成功。")
    context.logger.info("密码修改成功")


def action_launch_site(context: AppContext, site: str) -> None:
    """运行模式：注入 Cookie 并打开浏览器。

    Args:
        context: 运行期上下文。
        site: 目标网站域名。
    """
    print_title("正在启动浏览器")
    try:
        cookies = context.store.get(site)
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取 Cookie 数据。")
        context.logger.error("启动网站 %s 失败：cookies.enc 解密失败", site)
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("启动网站 %s 失败: %s", site, exc)
        return

    if not cookies:
        print_error("网站 {} 没有可用的 Cookie。".format(site))
        context.logger.warning("网站 %s 没有可用的 Cookie", site)
        return

    context.logger.info("用户选择网站: %s", site)
    try:
        run_session(site, cookies, context.config, context.logger)
        print_info("浏览器已关闭，Cookie 与临时数据已清理。")
    except BrowserError as exc:
        print_error(str(exc))
        context.logger.error("浏览器会话失败: %s", exc)
    except Exception as exc:  # 兜底，避免单个网站异常导致程序退出
        print_error("浏览器运行时出现异常: {}".format(exc))
        context.logger.error("浏览器运行时异常: %s\n%s", exc, traceback.format_exc())


# --------------------------------------------------------------------- 导出 Cookie（GitHub Action 保活配置）


def _prompt_second_password(context: AppContext) -> Optional[str]:
    """引导设置二重导出密码（首次使用导出功能时懒创建）。

    二重密码仅以 Argon2 哈希保存在 config.json 的 second_password_hash 字段，
    明文不落盘；用于保护导出 Cookie 操作。

    Args:
        context: 运行期上下文。

    Returns:
        设置成功的二重密码；设置失败返回 None。
    """
    print_title("设置二重导出密码")
    print_info("二重导出密码用于保护【导出 Cookie】操作：导出前必须输入该密码。")
    print_info("请勿使用与访问密码相同的密码。")

    while True:
        first = read_password("请输入二重导出密码（至少 {} 位）: ".format(MIN_PASSWORD_LENGTH))
        if len(first) < MIN_PASSWORD_LENGTH:
            print_error("密码长度不能少于 {} 位。".format(MIN_PASSWORD_LENGTH))
            continue
        if first != first.strip():
            print_error("密码首尾不能包含空格。")
            continue
        second = read_password("请再次输入二重导出密码: ")
        if first != second:
            print_error("两次输入的密码不一致，请重新输入。")
            continue
        break

    try:
        context.config["second_password_hash"] = crypto.hash_password(first)
        save_config(context.config)
    except Exception as exc:
        print_error("保存二重导出密码失败: {}".format(exc))
        context.logger.error("设置二重导出密码失败: %s", exc)
        return None

    context.logger.info("二重导出密码设置成功")
    print_info("二重导出密码设置成功。")
    return first


def action_export(context: AppContext) -> None:
    """导出 Cookie：校验二重密码后生成 GitHub Action 保活配置文件并打印 Fernet 密钥。

    流程：
    1. 二重密码未设置时引导创建（懒创建，仅存 Argon2 哈希）；
    2. 循环校验二重密码，错误可重试（Ctrl+C 取消由上层统一处理）；
    3. 解密 cookies.enc（只读，不修改存储）；
    4. 复用 core.exporter 生成 action_config_output.json（仅含密文）；
    5. 控制台打印 Fernet 密钥并给出 GitHub Secret / 改密码联动提醒。

    Args:
        context: 运行期上下文。
    """
    print_title("导出 Cookie（GitHub Action 保活配置）")
    print_info("[安全提示] 本操作会解密本地 Cookie 并生成密文配置与 Fernet 密钥；"
               "明文 Cookie 不会被打印或写入文件，导出产物仅含密文。")

    # 1. 二重密码：未设置则首次引导创建
    second_hash = str(context.config.get("second_password_hash") or "")
    if not second_hash:
        print_info("首次使用导出功能，请先设置二重导出密码。")
        if _prompt_second_password(context) is None:
            return
        second_hash = str(context.config.get("second_password_hash") or "")

    # 2. 循环校验二重密码（错误可重试，Ctrl+C 取消由上层处理）
    while True:
        entered = read_password("请输入二重导出密码: ")
        if not entered:
            print_error("密码不能为空，请重新输入。")
            continue
        if not crypto.verify_password(second_hash, entered):
            print_error("二重密码错误，请重试。")
            context.logger.warning("二重密码验证失败")
            continue
        break

    # 3. 读取本地 Cookie 数据（只读）
    try:
        data = context.store.load()
    except crypto.DecryptError:
        print_error("密码错误或数据损坏，无法读取 Cookie 数据。")
        context.logger.error("导出失败：cookies.enc 解密失败")
        return
    except StorageError as exc:
        print_error(str(exc))
        context.logger.error("导出失败: %s", exc)
        return

    # 4. 组装并写出输出文件（仅密文，明文 Cookie 只在内存）
    out_path = context.config_path.parent / exporter.OUTPUT_FILE_NAME
    try:
        exporter.export_action_config(
            data,
            context.key,
            out_path,
            on_skip=lambda site: print("跳过空 Cookie 列表的站点: {}".format(site)),
        )
    except exporter.ExporterError as exc:
        print_error(str(exc))
        context.logger.error("导出失败: %s", exc)
        return

    # 5. 打印 Fernet 密钥与提示
    key_str = context.key.decode("ascii")
    print()
    print("=" * 56)
    print("导出完成。")
    print()
    print("【GitHub Secret】以下 Fernet 密钥请保存到 GitHub Secret，")
    print("Secret 名称建议为：{}".format(exporter.GITHUB_SECRET_NAME))
    print("密钥（base64-urlsafe）:")
    print(key_str)
    print()
    print("【输出文件】已生成：{}".format(out_path))
    print("  该文件仅包含 Fernet 密文，不含明文 Cookie / encryption_salt / 密码，")
    print("  可以提交到 GitHub 仓库。")
    print()
    print("【重要警告】如果之后修改了 BiliCookieLauncher 访问密码，必须：")
    print("  1. 重新执行本导出功能")
    print("  2. 同步更新 GitHub Secret（{}）".format(exporter.GITHUB_SECRET_NAME))
    print("  3. 同步更新仓库中的 action_config_output.json")
    print("=" * 56)
    context.logger.info("导出 Cookie 配置成功（%d 个站点）", len(data))


# --------------------------------------------------------------------- 主菜单


def main_menu(context: AppContext) -> None:
    """主菜单：密码校验通过后统一入口，手动选择运行模式或修改模式。

    Args:
        context: 运行期上下文。
    """
    while True:
        print_title("Cookie 启动器")
        print("  1. 启动浏览器（加载 Cookie 打开网站）")
        print("  2. 进入修改模式（Cookie 管理）")
        print("  3. 导出 Cookie（GitHub Action 保活配置）")
        print("  0. 退出程序")
        choice = read_index("请选择 [0-3]: ", 0, 3)

        if choice == 0:
            print_info("已退出。")
            return
        if choice == 1:
            run_menu(context)
        elif choice == 3:
            action_export(context)
        else:
            modify_menu(context)


def modify_menu(context: AppContext) -> None:
    """修改模式主循环。

    Args:
        context: 运行期上下文。
    """
    while True:
        print_title("Cookie 管理器")
        print("  1. 添加或替换网站 Cookie")
        print("  2. 查看已配置网站")
        print("  3. 删除网站 Cookie")
        print("  4. 修改密码")
        print("  5. 返回主菜单")
        choice = read_index("请选择 [1-5]: ", 1, 5)

        if choice == 1:
            action_add_site(context)
        elif choice == 2:
            action_list_sites(context)
        elif choice == 3:
            action_delete_site(context)
        elif choice == 4:
            action_change_password(context)
        else:
            print_info("已返回主菜单。")
            return


def run_menu(context: AppContext) -> None:
    """运行模式（启动浏览器）循环。

    Args:
        context: 运行期上下文。
    """
    while True:
        print_title("Cookie 启动器")
        try:
            sites = context.store.list_sites()
        except crypto.DecryptError:
            print_error("密码错误或数据损坏，无法读取 Cookie 数据。")
            context.logger.error("读取网站列表失败：cookies.enc 解密失败")
            return
        except StorageError as exc:
            print_error(str(exc))
            context.logger.error("读取网站列表失败: %s", exc)
            return

        if not sites:
            print_info("暂无已配置的网站。")
            print_info("请返回主菜单选择 2 进入修改模式，先添加 Cookie。")
            return

        print_info("已配置网站：")
        for index, (site, count) in enumerate(sites, start=1):
            print("  {}. {}（{} 个 Cookie）".format(index, site, count))
        print("  0. 退出")

        choice = read_index("请选择要打开的网站 [0-{}]: ".format(len(sites)), 0, len(sites))
        if choice == 0:
            print_info("已退出。")
            return

        site = sites[choice - 1][0]
        action_launch_site(context, site)
