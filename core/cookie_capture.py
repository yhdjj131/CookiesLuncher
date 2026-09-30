"""浏览器手动登录 Cookie 采集模块（仅负责采集，不做持久化加密存储）。

【重要风险注释】
注意：本模块使用 ``executable_path`` 调用 Thorium（第三方 Chromium 衍生浏览器）；
Playwright 官方仅保证对自带 Chromium 支持；CDP 版本不匹配会引发连接失败、
无法采集 Cookie 的风险。若采集失败，请优先检查浏览器版本与 Playwright 的兼容性。

设计约定：
- 采集阶段**不使用** Playwright 捆绑的自带 Chromium 二进制，统一复用项目内嵌
  Thorium（thorium 后端）或本机 Edge/Chrome（system 后端）；
- 采集流程生成**独立临时 user-data-dir**（前缀 ``cookie_capture_``），
  不与浏览流程（``browser.run_session`` 的 ``cookie_launcher_``）共享 profile；
- 仅由用户控制台回车确认结束采集，不做页面自动登录检测；
  浏览器必须 ``headless=False`` 可见，供用户手动登录/验证码；
- 采集完成后调用 ``context.close()`` 完整关闭浏览器上下文，并由 ``finally``
  强制清理：psutil 按 user-data-dir 查杀残留进程 + 安全删除临时目录；
  无论正常返回、异常崩溃都要清理，不能遗留目录/进程；
- 本模块不写入 cookies.enc；持久化由 UI 层调用 storage 完成，
  采集返回空 Cookie 列表时拒绝保存。
- **采集结果不做域名过滤，全部有效 Cookie 一律保留**（不再按目标站点
  域名匹配过滤，避免登录态采集不完整）；仅剔除无法构成合法条目的数据
  （缺少 name/value/domain）与完全重复的条目（同名同 domain 同 path）。
"""

from __future__ import annotations

import shutil
import tempfile
import time
from typing import Any, Dict, List, Optional, Tuple

from .browser import FIXED_LAUNCH_ARGS, get_browser_executable

# 采集专用临时目录前缀（与浏览流程 cookie_launcher_ 区分，禁止共用 profile）
CAPTURE_TEMP_PREFIX = "cookie_capture_"

# 页面初始加载超时（秒）
_CAPTURE_GOTO_TIMEOUT = 60.0

# 残留进程查杀等待与目录删除重试超时（秒）
_KILL_WAIT_TIMEOUT = 10.0
_REMOVE_RETRY_TIMEOUT = 15.0


class CookieCaptureError(Exception):
    """浏览器采集失败（Playwright 缺失 / 浏览器启动失败 / CDP 错误）。"""


# ------------------------------------------------------------------ 转换（纯函数，便于单元测试）


def normalize_captured_cookies(
    pw_cookies: List[Dict[str, Any]]
) -> Tuple[List[Dict[str, Any]], List[str]]:
    """把 Playwright ``context.cookies()`` 结果转换为内部存储结构。

    转换规则：
    - 只保留内部结构认识的字段：name / value / domain / path / expires /
      httpOnly / secure / sameSite；
    - ``expires``（秒，可为 float）转整型；<=0 的会话 Cookie 省略 expires；
    - ``sameSite`` 直通（Strict / Lax / None）；
    - **不做域名过滤**：所有有效 Cookie 一律保留（不再按目标站点匹配），
      仅剔除无法构成合法条目的数据（缺少 name/value/domain）与完全重复的
      条目（同名同 domain 同 path）。

    Args:
        pw_cookies: Playwright 返回的 Cookie 列表。

    Returns:
        (内部结构 Cookie 列表, 剔除原因列表)。
    """
    result: List[Dict[str, Any]] = []
    dropped: List[str] = []
    seen = set()

    for cookie in pw_cookies:
        if not isinstance(cookie, dict):
            dropped.append("剔除非法条目（非对象）")
            continue

        name = cookie.get("name")
        value = cookie.get("value")
        domain = cookie.get("domain")
        if not name or value is None or not domain:
            dropped.append("剔除缺少 name/value/domain 的条目: {}".format(name or "(无名称)"))
            continue

        domain = str(domain).strip()

        path = str(cookie.get("path") or "/")
        if not path.startswith("/"):
            path = "/" + path

        key = (str(name), domain, path)
        if key in seen:
            dropped.append("剔除重复条目: {}({}{})".format(name, domain, path))
            continue
        seen.add(key)

        item: Dict[str, Any] = {
            "name": str(name),
            "value": str(value),
            "domain": domain,
            "path": path,
        }

        expires = cookie.get("expires")
        if isinstance(expires, (int, float)) and not isinstance(expires, bool) and expires > 0:
            item["expires"] = int(expires)

        if cookie.get("httpOnly"):
            item["httpOnly"] = True
        if cookie.get("secure"):
            item["secure"] = True

        same_site = cookie.get("sameSite")
        if same_site in ("Strict", "Lax", "None"):
            item["sameSite"] = same_site

        result.append(item)

    return result, dropped


# ------------------------------------------------------------------ 进程与目录清理


def _kill_residual_processes(user_data_dir: str, logger: Any = None) -> None:
    """用 psutil 按 user-data-dir 匹配查杀残留的浏览器进程（thorium/system 通用）。

    仅清理命令行中包含本次临时 user-data-dir 的进程，不会误伤用户
    自行打开的其它浏览器实例。

    Args:
        user_data_dir: 本次采集使用的临时用户数据目录。
        logger: 日志记录器（可选）。
    """
    try:
        import psutil
    except ImportError:
        if logger is not None:
            logger.warning("未安装 psutil，跳过残留进程查杀")
        return

    udd = str(user_data_dir).lower()
    deadline = time.time() + _KILL_WAIT_TIMEOUT
    while time.time() < deadline:
        alive = []
        for proc in psutil.process_iter(["pid", "name", "cmdline"]):
            try:
                cmdline = proc.info.get("cmdline") or []
                if any(udd in str(part).lower() for part in cmdline):
                    alive.append(proc)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        if not alive:
            return
        for proc in alive:
            try:
                proc.terminate()
            except Exception:
                pass
        time.sleep(0.3)

    # 超时仍未退出，升级为强杀
    for proc in psutil.process_iter(["pid", "name", "cmdline"]):
        try:
            cmdline = proc.info.get("cmdline") or []
            if any(udd in str(part).lower() for part in cmdline):
                try:
                    proc.kill()
                except Exception:
                    pass
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    if logger is not None:
        logger.warning("残留浏览器进程未完全退出，已强制终止")


def _remove_dir_retry(path: str) -> None:
    """安全删除临时目录，失败重试直到超时（文件占用通常因进程未完全退出）。"""
    deadline = time.time() + _REMOVE_RETRY_TIMEOUT
    while True:
        try:
            shutil.rmtree(path)
            return
        except OSError:
            if time.time() >= deadline:
                shutil.rmtree(path, ignore_errors=True)
                return
            time.sleep(0.3)


# ------------------------------------------------------------------ 采集主流程


def capture_cookies(
    url: str, config: Dict[str, Any], logger: Any
) -> List[Dict[str, Any]]:
    """启动可见浏览器供用户手动登录，采集并返回规范化后的 Cookie 列表。

    流程：
    1. 按 config 的 browser_backend 解析浏览器可执行文件（thorium 内嵌 /
       system 本机 Edge/Chrome），启动参数复用 ``browser.FIXED_LAUNCH_ARGS``；
    2. 创建**独立临时 user-data-dir**（``cookie_capture_`` 前缀）；
    3. ``launch_persistent_context(executable_path=..., headless=False)``
       打开登录 URL，等待用户手动登录；**仅控制台回车确认**结束；
    4. ``context.cookies()`` 读取 -> 转换（normalize_captured_cookies，
       全部有效 Cookie 一律保留，不做域名过滤）；
    5. ``context.close()`` 完整关闭浏览器上下文；
    6. ``finally`` 强制：psutil 查杀残留进程 + 安全删除临时目录。

    Args:
        url: 用户输入的登录 URL（无协议时自动补 https://）。
        config: 完整配置字典（browser_backend 决定 thorium / system 后端）。
        logger: 日志记录器。

    Returns:
        采集到的 Cookie 列表（已转换，全部保留）；用户取消或失败时返回空列表。

    Raises:
        CookieCaptureError: Playwright 不可用、浏览器启动失败或 CDP 错误。
    """
    try:
        executable = get_browser_executable(config)
    except Exception as exc:
        raise CookieCaptureError("无法定位浏览器可执行文件: {}".format(exc)) from exc

    normalized_url = url if "://" in url else "https://" + url

    user_data_dir = tempfile.mkdtemp(prefix=CAPTURE_TEMP_PREFIX)
    context: Any = None
    try:
        try:
            from playwright.sync_api import sync_playwright
        except ImportError as exc:  # pragma: no cover - 依赖缺失
            raise CookieCaptureError(
                "未安装 Playwright（Cookie 采集依赖它）：请先 pip install -r requirements.txt"
            ) from exc

        try:
            with sync_playwright() as p:
                context = p.chromium.launch_persistent_context(
                    user_data_dir=user_data_dir,
                    executable_path=str(executable),
                    headless=False,  # 必须可见，供用户手动登录/验证码
                    args=list(FIXED_LAUNCH_ARGS),
                    no_viewport=True,
                )
                page = context.pages[0] if context.pages else context.new_page()
                page.goto(
                    normalized_url,
                    wait_until="domcontentloaded",
                    timeout=int(_CAPTURE_GOTO_TIMEOUT * 1000),
                )
                logger.info("采集浏览器已打开: %s", normalized_url)

                print("请在浏览器中手动登录（账号/验证码）。")
                print("登录完成后，回到本窗口按【回车】确认并采集 Cookie。")
                try:
                    input()
                except EOFError:
                    raise CookieCaptureError("输入流中断，采集已取消")

                pw_cookies = context.cookies()
                cookies, dropped = normalize_captured_cookies(pw_cookies)
                for message in dropped:
                    logger.info("采集剔除: %s", message)
                logger.info(
                    "采集到 Cookie: %d 条（全部保留），剔除 %d 条",
                    len(cookies),
                    len(dropped),
                )
                return cookies
        except CookieCaptureError:
            raise
        except Exception as exc:
            # CDP 协议错误 / 浏览器启动失败 / 上下文已断开等统一归为采集失败
            raise CookieCaptureError("浏览器采集失败（CDP 或浏览器错误）: {}".format(exc)) from exc
    finally:
        if context is not None:
            try:
                context.close()
            except Exception:
                pass
        _kill_residual_processes(user_data_dir, logger)
        _remove_dir_retry(user_data_dir)
        logger.info("采集临时目录与进程已清理")
