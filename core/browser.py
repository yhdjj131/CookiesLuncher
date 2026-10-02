"""浏览器模块：双后端（thorium / system）启动 Chromium 内核浏览器、注入 Cookie、退出时清理痕迹。

方案C（当前实现，折中方案）：
- **Playwright 仅用于启动 persistent_context 并注入 Cookie**：用
  ``launch_persistent_context`` 绑定动态生成的临时 user-data-dir，执行 ``add_cookies``
  把 Cookie 交给 Chromium 自身持久化写入磁盘 profile（加密 Blob 由 Chromium 原生生成，
  合法有效，登录态可靠；不再手写 SQLite / DPAPI / v10 加密——方案B 已证实不可行，
  外部程序无法生成合法 DPAPI 加密 Blob，会导致 B 站登录失败）。
- **页面加载完成后退出 Playwright 上下文**：context.close() 时 Chromium 正常关闭，
  Cookie 已落盘；随后由 ``subprocess.Popen`` 用**同一个 profile** 直接启动浏览器，
  浏览器独立运行（不依赖 Playwright / CDP 连接），由用户正常关闭。
- 不使用方案A 的 JS 修补 / click 拦截（会破坏页面跳转交互），``target="_blank"``
  保持原生行为，不出现 about:blank#blocked。
- **进程树监控**：用 psutil 监控主 PID 及全部递归子进程，等待整套浏览器进程
  全部退出后再删除临时 profile 目录（规避文件占用）。
- thorium / system 两套后端共用同一套逻辑与参数。
- 启动时清理程序异常退出遗留的 ``cookie_launcher_*`` 临时目录。

后端说明：
- ``thorium``（默认）：使用随程序打包的社区编译 Thorium（win64，自带 H.264/AAC 专有编解码），
  离线自包含，目标机无需预装任何浏览器；
- ``system``：探测本机 Edge / Chrome 并复用其浏览器，用于 DRM 会员视频等场景，非离线模式。
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

from .config import BACKEND_SYSTEM, BACKEND_THORIUM, is_maximized, parse_window_size
from .cookie_parser import normalize_site

# 临时用户数据目录前缀
TEMP_DIR_PREFIX = "cookie_launcher_"

# 无头模式下的等待时间（秒），无头时没有窗口可关闭
HEADLESS_GRACE_SECONDS = 5.0

# 固定窗口尺寸时 Chromium 的窗口尺寸参数
_WINDOW_ARG_TEMPLATE = "--window-size={},{}"

# thorium 内置目录约定：external_browsers/thorium-win64/thorium.exe
THORIUM_DIR_NAME = "thorium-win64"
THORIUM_EXE_NAME = "thorium.exe"

# 所有后端共用的固定启动参数（thorium / system 完全一致）
FIXED_LAUNCH_ARGS = [
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-quic",
    "--disable-background-networking",
    "--disable-sync",
    "--enable-gpu-rasterization",
    "--ignore-gpu-blocklist",
    "--disable-blink-features=AutomationControlled",
    # B 站播放兼容：关闭站点隔离/分区 Cookie/弹窗拦截/沙箱限制，允许自动播放
    "--disable-features=IsolateOrigins,site-per-process,PartitionedCookies",
    "--disable-popup-blocking",
    "--disable-site-isolation-trials",
    "--no-sandbox",
    "--disable-dev-shm-usage",
    "--autoplay-policy=no-user-gesture-required",
]

# 等待浏览器进程树全部退出的总超时（秒）
_EXIT_WAIT_TIMEOUT = 45.0
# 删除临时目录的重试超时（秒）
_REMOVE_DIR_TIMEOUT = 15.0

# Playwright 注入 Cookie 后页面加载完成的稳定等待（秒）
_SEED_LOAD_STABLE_SECONDS = 1.5
# Playwright 打开页面（含浏览器冷启动）的超时（秒）
_SEED_GOTO_TIMEOUT = 60.0
# Playwright 阶段无头模式（不闪现窗口，Cookie 落盘行为与有头一致）
_SEED_HEADLESS = True

# system 后端探测的本机浏览器常见路径（注册表之外的第二道检查）
SYSTEM_BROWSER_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]

# 注册表 App Paths 探测键（按优先级排列）
_SYSTEM_REG_KEYS = [
    r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
    r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
    r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
    r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
]


class BrowserError(Exception):
    """浏览器启动或操作失败。"""


def _to_playwright_cookie(cookie: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    """把业务 Cookie 字典转换为 Playwright ``add_cookies`` 接受的字段。

    转换规则：
    - ``expires`` / ``expirationDate``（秒，可为 float）→ 整型 ``expires``；
      无过期时间的会话 Cookie 不传 ``expires``（Chromium 按会话处理）；
    - ``sameSite`` 仅接受 Playwright 枚举：Strict / Lax / None（None 表示不设置）；
    - 缺失 name / value / domain 的条目返回 None（由调用方跳过）。

    Args:
        cookie: 业务 Cookie 字典。

    Returns:
        Playwright Cookie 字典；无效条目返回 None。
    """
    name = cookie.get("name")
    value = cookie.get("value")
    domain = cookie.get("domain") or cookie.get("host")
    if not name or value is None or not domain:
        return None

    out: Dict[str, Any] = {
        "name": str(name),
        "value": str(value),
        "domain": str(domain).strip(),
        "path": str(cookie.get("path") or "/"),
    }
    expires = cookie.get("expires") or cookie.get("expirationDate")
    if expires:
        try:
            out["expires"] = int(float(expires))
        except (TypeError, ValueError):
            pass
    if cookie.get("httpOnly") or cookie.get("httponly"):
        out["httpOnly"] = True
    if cookie.get("secure"):
        out["secure"] = True
    samesite = cookie.get("sameSite")
    if samesite in ("Strict", "Lax", "None"):
        out["sameSite"] = samesite
    return out


def _seed_profile_with_playwright(
    user_data_dir: str,
    cookies: List[Dict[str, Any]],
    executable: Path,
    url: str,
    logger: Any,
) -> int:
    """阶段1：用 Playwright 启动 persistent_context，注入 Cookie 后关闭。

    Chromium 的 ``add_cookies`` 走原生链路：Cookie 由浏览器进程自己加密并持久化
    写入 profile 的 ``Default/Network/Cookies``（加密 Blob 完全合法，登录态可靠）。
    context.close() 时浏览器正常关闭，Cookie 已完成落盘。

    Args:
        user_data_dir: 临时用户数据目录（同一目录稍后由 subprocess 复用）。
        cookies: 该网站的 Cookie 列表。
        executable: 浏览器可执行文件路径。
        url: 用于加载确认的首页 URL。
        logger: 日志记录器。

    Returns:
        成功注入的 Cookie 条数。

    Raises:
        BrowserError: Playwright 不可用、浏览器启动失败或页面加载失败。
    """
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as exc:  # pragma: no cover - 依赖缺失
        raise BrowserError(
            "未安装 Playwright（Cookie 注入依赖它）：请先 pip install -r requirements.txt"
        ) from exc

    pw_cookies = [_to_playwright_cookie(c) for c in cookies]
    pw_cookies = [c for c in pw_cookies if c is not None]
    if not pw_cookies:
        raise BrowserError("没有可注入的 Cookie（缺少 name / value / domain）")

    try:
        with sync_playwright() as p:
            context = p.chromium.launch_persistent_context(
                user_data_dir=user_data_dir,
                executable_path=str(executable),
                headless=_SEED_HEADLESS,
                args=list(FIXED_LAUNCH_ARGS),
                no_viewport=True,
            )
            try:
                context.add_cookies(pw_cookies)
                logger.info("已用 Playwright 注入 Cookie: %d 条", len(pw_cookies))
                page = context.new_page()
                page.goto(url, wait_until="domcontentloaded", timeout=int(_SEED_GOTO_TIMEOUT * 1000))
                page.wait_for_timeout(int(_SEED_LOAD_STABLE_SECONDS * 1000))
                logger.info("页面加载完成，Cookie 已落盘: %s", url)
            finally:
                context.close()
        return len(pw_cookies)
    except BrowserError:
        raise
    except Exception as exc:
        raise BrowserError("Playwright Cookie 注入失败: {}".format(exc)) from exc


def _build_launch_command(
    executable: Path,
    user_data_dir: str,
    config: Dict[str, Any],
    url: str,
    extra_args: Optional[List[str]] = None,
) -> List[str]:
    """组装浏览器启动命令行（thorium / system 共用同一套参数逻辑）。

    Args:
        executable: 浏览器可执行文件路径。
        user_data_dir: 临时用户数据目录（Playwright 注入 Cookie 后的同一目录）。
        config: 完整配置字典。
        url: 首页 URL（追加为命令行最后一个参数）。
        extra_args: 额外参数（测试脚本可传 --remote-debugging-port 等）。

    Returns:
        完整命令行列表。
    """
    browser_config = config.get("browser") or {}
    headless = bool(browser_config.get("headless", False))
    window_size = browser_config.get("window_size", "maximized")

    args = list(FIXED_LAUNCH_ARGS)
    args.append("--user-data-dir=" + user_data_dir)
    if headless:
        args.append("--headless=new")

    size = parse_window_size(window_size)
    if is_maximized(window_size) or size is None:
        args.append("--start-maximized")
    else:
        args.append(_WINDOW_ARG_TEMPLATE.format(size[0], size[1]))

    if extra_args:
        args.extend(extra_args)

    command = [str(executable)] + args
    if url:
        command.append(url)
    return command


def _wait_process_tree_exit(
    pid: int, logger: Any, timeout: float = _EXIT_WAIT_TIMEOUT
) -> bool:
    """用 psutil 监控主 PID 及全部递归子进程，等待整套进程树退出。

    Args:
        pid: 浏览器主进程 PID。
        logger: 日志记录器。
        timeout: 总等待超时（秒）。

    Returns:
        True 表示进程树已全部退出；False 表示超时（已尝试终止主进程）。
    """
    try:
        import psutil
    except ImportError:
        logger.warning("未安装 psutil，退化为直接探测主进程状态")
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                os.kill(pid, 0)
            except OSError:
                return True
            time.sleep(0.5)
        return False

    deadline = time.time() + timeout
    try:
        parent = psutil.Process(pid)
    except psutil.NoSuchProcess:
        return True

    while time.time() < deadline:
        try:
            if not parent.is_running():
                return True
        except psutil.NoSuchProcess:
            return True
        alive: List[Any] = []
        try:
            alive = [
                child
                for child in parent.children(recursive=True)
                if child.is_running()
            ]
        except psutil.NoSuchProcess:
            return True
        except Exception:
            pass
        if not alive:
            return True
        time.sleep(0.5)

    logger.warning("等待 %.0f 秒后浏览器进程树仍未完全退出，尝试终止", timeout)
    try:
        parent.terminate()
        parent.wait(timeout=5)
    except Exception:
        pass
    return False


def _remove_dir_retry(path: str, logger: Any, timeout: float = _REMOVE_DIR_TIMEOUT) -> None:
    """删除临时目录，失败重试直到超时（文件占用通常因进程未完全退出）。"""
    deadline = time.time() + timeout
    while True:
        try:
            shutil.rmtree(path)
            logger.info("临时目录已清理")
            return
        except OSError:
            if time.time() >= deadline:
                shutil.rmtree(path, ignore_errors=True)
                if Path(path).exists():
                    logger.warning("临时目录删除失败（可能仍被进程占用）: %s", path)
                else:
                    logger.info("临时目录已清理")
                return
            time.sleep(0.5)


def cleanup_stale_temp_dirs(logger: Any = None) -> int:
    """清理程序异常退出残留的 ``cookie_launcher_*`` 临时目录（位于系统 %TEMP%）。

    Returns:
        清理的目录数量。
    """
    removed = 0
    try:
        temp_root = Path(tempfile.gettempdir())
        for d in temp_root.glob(TEMP_DIR_PREFIX + "*"):
            if not d.is_dir():
                continue
            try:
                shutil.rmtree(d)
                removed += 1
                if logger is not None:
                    logger.info("已清理遗留临时目录: %s", d)
            except OSError:
                if logger is not None:
                    logger.warning("遗留临时目录清理失败（可能被占用）: %s", d)
    except Exception:
        pass
    return removed


def _app_root() -> Path:
    """返回程序根目录。

    打包后为可执行文件所在目录，源码运行时为项目根（core 包的上一级）。
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def _thorium_exe_path() -> Path:
    """thorium 后端的可执行文件路径（区分源码 / 打包环境）。

    - 源码：``bili_cookie_launcher/external_browsers/thorium-win64/thorium.exe``
    - 打包：PyInstaller 通过 --add-data 把 external_browsers 打入
      ``_MEIPASS/external_browsers/thorium-win64/thorium.exe``
    """
    meipass = getattr(sys, "_MEIPASS", None)
    if meipass:
        base = Path(meipass)
    else:
        base = _app_root()
    return base / "external_browsers" / THORIUM_DIR_NAME / THORIUM_EXE_NAME


def _probe_system_browser() -> Optional[Path]:
    """探测本机 Edge / Chrome 的可执行文件。

    先查注册表 ``App Paths``（HKLM + HKCU），再查常见安装路径。

    Returns:
        找到的可执行文件路径；未找到返回 None。
    """
    try:
        import winreg
    except ImportError:  # pragma: no cover - 仅非 Windows 环境
        winreg = None

    if winreg is not None:
        for key_path in _SYSTEM_REG_KEYS:
            for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
                try:
                    with winreg.OpenKey(hive, key_path) as key:
                        value, _ = winreg.QueryValueEx(key, "")
                except OSError:
                    continue
                if value and Path(value).is_file():
                    return Path(value)

    for candidate in SYSTEM_BROWSER_CANDIDATES:
        if Path(candidate).is_file():
            return Path(candidate)
    return None


def get_browser_executable(cfg: Dict[str, Any]) -> Path:
    """根据 ``browser_backend`` 返回浏览器可执行文件路径。

    Args:
        cfg: 完整配置字典（含 browser_backend）。

    Returns:
        可执行文件路径。

    Raises:
        BrowserError: thorium 文件缺失，或 system 未探测到本机浏览器。
    """
    backend = str(cfg.get("browser_backend") or BACKEND_THORIUM).strip().lower()
    if backend == BACKEND_SYSTEM:
        found = _probe_system_browser()
        if found is None:
            raise BrowserError(
                "未检测到本机 Edge/Chrome（system 后端依赖本机浏览器，不是离线自包含）。"
                "请安装 Microsoft Edge 或 Google Chrome 后重试，或把 config.json 的 "
                "browser_backend 改回 thorium。"
            )
        return found

    # 默认 thorium
    path = _thorium_exe_path()
    if not path.is_file():
        if getattr(sys, "_MEIPASS", None):
            hint = "打包内置路径 _internal/external_browsers/thorium-win64/thorium.exe，请重新打包"
        else:
            hint = "源码目录 external_browsers/thorium-win64/thorium.exe，请先放置 Thorium"
        raise BrowserError("未找到内置 Thorium 浏览器（{}）。".format(hint))
    return path


def setup_playwright_env() -> None:
    """程序启动阶段调用：清理历史遗留的临时目录（保留签名兼容 main.py）。

    方案C 中 Playwright 是业务 Cookie 注入的运行依赖（无需额外环境准备）；
    本函数顺带清理上次异常退出残留的 ``cookie_launcher_*`` 临时目录。
    """
    cleanup_stale_temp_dirs()
    return None


def run_session(
    site: str,
    cookies: List[Dict[str, Any]],
    config: Dict[str, Any],
    logger: Any,
    extra_args: Optional[List[str]] = None,
) -> None:
    """启动浏览器（Playwright 注入 Cookie → 浏览器独立运行），关闭后清理全部痕迹。

    流程（方案C）：
    1. 创建全新临时 user-data-dir；
    2. **阶段1**：Playwright ``launch_persistent_context`` 绑定该目录，``add_cookies``
       注入 Cookie（Chromium 原生持久化落盘），打开首页确认页面加载完成后
       **退出 Playwright 上下文**（浏览器随之关闭，但 Cookie 已写盘）；
    3. **阶段2**：``subprocess.Popen`` 用**同一个 profile** 直接启动浏览器，
       独立运行、断开 CDP，等待用户关闭（无头模式定时关闭）；
    4. psutil 监控进程树全部退出后删除临时目录。

    Args:
        site: 网站域名。
        cookies: 该网站的 Cookie 列表。
        config: 完整配置字典。
        logger: 日志记录器。
        extra_args: 额外启动参数（测试脚本可传 --remote-debugging-port）。

    Raises:
        BrowserError: 浏览器启动或 Cookie 注入失败。
    """
    normalized_site = normalize_site(site)
    valid = [c for c in cookies if c.get("name") and (c.get("domain") or c.get("host"))]
    if not valid:
        raise BrowserError("没有可用的 Cookie（缺少 name 或 domain），无法启动浏览器")

    backend = str(config.get("browser_backend") or BACKEND_THORIUM).strip().lower()
    executable = get_browser_executable(config)

    if backend == BACKEND_SYSTEM:
        print(
            "[提示] system 模式使用本机浏览器（{}），依赖本机登录态，不是离线自包含。"
            "会员/DRM 视频请确保已在对应浏览器登录。".format(executable)
        )
        logger.info("system 后端使用本机浏览器: %s", executable)
    else:
        logger.info("thorium 后端使用内置浏览器: %s", executable)

    browser_config = config.get("browser") or {}
    headless = bool(browser_config.get("headless", False))
    url = "https://{}".format(normalized_site)

    user_data_dir = tempfile.mkdtemp(prefix=TEMP_DIR_PREFIX)
    proc: Optional[subprocess.Popen] = None
    cleaned = False
    try:
        # 阶段1：Playwright 注入 Cookie 并落盘（profile 由 Chromium 原生写库）
        _seed_profile_with_playwright(user_data_dir, valid, executable, url, logger)

        # 阶段2：同一 profile 由 subprocess 直接启动，浏览器独立运行
        command = _build_launch_command(executable, user_data_dir, config, url, extra_args)
        logger.info("启动命令: %s", " ".join(command))
        creationflags = 0
        if sys.platform == "win32":
            creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        try:
            proc = subprocess.Popen(command, creationflags=creationflags)
        except OSError as exc:
            raise BrowserError("浏览器启动失败，请检查浏览器是否完整: {}".format(exc))
        logger.info("浏览器已启动 (pid=%d)", proc.pid)

        # 等待关闭
        if headless:
            logger.info("无头模式：%.0f 秒后自动关闭浏览器", HEADLESS_GRACE_SECONDS)
            try:
                time.sleep(HEADLESS_GRACE_SECONDS)
            except KeyboardInterrupt:
                pass
            try:
                proc.terminate()
            except Exception:
                pass
            try:
                proc.wait(timeout=15)
            except Exception:
                pass
        else:
            logger.info("等待用户关闭浏览器...")
            try:
                proc.wait()
            except KeyboardInterrupt:
                logger.info("收到中断信号，正在关闭浏览器...")
                try:
                    proc.terminate()
                except Exception:
                    pass
                try:
                    proc.wait(timeout=10)
                except Exception:
                    pass
                raise
        logger.info("浏览器已关闭")

        # 进程树全部退出后删除临时目录
        _wait_process_tree_exit(proc.pid, logger)
        _remove_dir_retry(user_data_dir, logger)
        cleaned = True
    except BrowserError:
        raise
    except Exception as exc:
        raise BrowserError("浏览器启动失败，请检查浏览器是否完整: {}".format(exc))
    finally:
        if not cleaned:
            # 异常或 Ctrl+C 时也要尽力清理，失败不阻塞退出
            if proc is not None and proc.poll() is None:
                try:
                    proc.terminate()
                except Exception:
                    pass
                try:
                    proc.wait(timeout=8)
                except Exception:
                    pass
            try:
                _wait_process_tree_exit(proc.pid, logger, timeout=10)
            except Exception:
                pass
            shutil.rmtree(user_data_dir, ignore_errors=True)
            logger.info("已执行应急清理")
