"""Cookie 文本解析与格式统一。

支持三种输入格式，自动识别：

1. 分号分隔字符串：``SESSDATA=abc; bili_jct=def``
2. 标准 JSON 数组：``[{"name": ..., "value": ...}]``
3. Cookie-Editor 导出的 JSON（含 hostOnly / session / storeId / expirationDate 等额外字段）

解析结果统一为 Playwright 的 Cookie 结构：
``{"name", "value", "domain", "path", "expires", "httpOnly", "secure", "sameSite"}``
其中除 name / value / domain / path 外均为可选字段。
"""

from __future__ import annotations

import json
import re
from typing import Any, Dict, List, Optional, Tuple

# 允许出现在 Cookie 字典中的字段
COOKIE_FIELDS = (
    "name",
    "value",
    "domain",
    "path",
    "expires",
    "httpOnly",
    "secure",
    "sameSite",
)

# sameSite 取值映射：Cookie-Editor / 浏览器导出 -> Playwright
# 值为 None 表示该字段需要省略（例如 unspecified）
SAME_SITE_MAP: Dict[str, Optional[str]] = {
    "no_restriction": "None",
    "none": "None",
    "unspecified": None,
    "lax": "Lax",
    "strict": "Strict",
}

# 网站域名合法字符（支持 localhost、IPv4、多级域名）
_SITE_RE = re.compile(r"^[a-z0-9]([a-z0-9_\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9_\-]*[a-z0-9])?)*$")

# 提示信息中展示的最大片段长度，避免把完整 Cookie 打到控制台
_PREVIEW_LIMIT = 20


class CookieParseError(ValueError):
    """Cookie 解析失败。"""


def _preview(text: str) -> str:
    """截断文本用于提示信息，避免泄露完整 Cookie 内容。"""
    text = str(text).strip()
    if len(text) <= _PREVIEW_LIMIT:
        return text
    return text[:_PREVIEW_LIMIT] + "..."


def normalize_site(raw: str) -> str:
    """把用户输入的网站地址规范化为纯域名。

    支持 ``bilibili.com``、``.bilibili.com``、``https://www.bilibili.com/xxx``、
    ``bilibili.com:443`` 等写法，统一返回不带前导点、小写的域名。

    Args:
        raw: 用户输入的网站域名或 URL。

    Returns:
        规范化后的域名，例如 ``bilibili.com``。

    Raises:
        CookieParseError: 输入为空或格式非法。
    """
    text = "" if raw is None else str(raw).strip().lower()
    if not text:
        raise CookieParseError("网站域名不能为空")

    if "://" in text:
        text = text.split("://", 1)[1]
    text = text.split("/", 1)[0]
    text = text.split("?", 1)[0]
    text = text.split("#", 1)[0]
    if "@" in text:
        text = text.rsplit("@", 1)[1]
    if text.startswith("["):
        raise CookieParseError("暂不支持 IPv6 地址作为网站域名")
    if ":" in text:
        text = text.split(":", 1)[0]
    text = text.strip().strip(".")

    if not text:
        raise CookieParseError("网站域名不能为空")
    if not _SITE_RE.match(text):
        raise CookieParseError("网站域名格式不正确: {}".format(_preview(text)))
    return text


def cookie_domain(site: str) -> str:
    """由网站域名生成 Cookie 的 domain 值（前缀加 ``.``）。"""
    return "." + site.lstrip(".")


def site_from_cookie_domain(domain: str) -> str:
    """由 Cookie 的 domain 还原网站域名（去掉前导点）。"""
    return str(domain or "").strip().lower().lstrip(".")


def detect_format(raw: str) -> str:
    """判断 Cookie 文本属于哪种格式。

    Args:
        raw: 原始文本。

    Returns:
        ``"json"`` 或 ``"semicolon"``。

    Raises:
        CookieParseError: 两种格式都不匹配。
    """
    text = (raw or "").strip()
    if not text:
        raise CookieParseError("Cookie 内容不能为空")
    if text.startswith("[") or text.startswith("{"):
        return "json"
    if ";" in text and "=" in text:
        return "semicolon"
    if "=" in text:
        # 只有一条 Cookie，没有分号，也按分号格式处理
        return "semicolon"
    raise CookieParseError("无法识别的 Cookie 格式，支持分号格式和 JSON 格式")


def _parse_semicolon(raw: str, default_domain: str, warnings: List[str]) -> List[Dict[str, Any]]:
    """解析分号分隔的 Cookie 字符串。

    Args:
        raw: 原始文本，例如 ``a=1; b=2``。
        default_domain: 用户输入的网站对应的 Cookie domain。
        warnings: 用于收集跳过原因的列表。

    Returns:
        Cookie 字典列表。
    """
    cookies: List[Dict[str, Any]] = []
    seen: Dict[str, int] = {}
    segments = raw.split(";")
    for index, segment in enumerate(segments, start=1):
        segment = segment.strip()
        if not segment:
            continue
        if "=" not in segment:
            warnings.append("第 {} 个片段缺少 = 分隔符，已跳过: {}".format(index, _preview(segment)))
            continue
        name, _, value = segment.partition("=")
        name = name.strip()
        value = value.strip()
        if not name:
            warnings.append("第 {} 个片段缺少 Cookie 名称，已跳过".format(index))
            continue
        # 去掉成对的引号
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        if name in seen:
            warnings.append("Cookie 名称重复，已使用最后一个值: {}".format(name))
            cookies[seen[name]] = {
                "name": name,
                "value": value,
                "domain": default_domain,
                "path": "/",
            }
            continue
        seen[name] = len(cookies)
        cookies.append(
            {
                "name": name,
                "value": value,
                "domain": default_domain,
                "path": "/",
            }
        )
    return cookies


def _normalize_item(
    item: Any, default_domain: str, position: int, warnings: List[str]
) -> Optional[Dict[str, Any]]:
    """把单个 JSON 对象规范化为 Playwright Cookie 结构。

    Args:
        item: JSON 中的一条记录。
        default_domain: 缺省 domain。
        position: 记录序号（从 1 开始），用于提示。
        warnings: 用于收集跳过原因的列表。

    Returns:
        规范化后的 Cookie 字典；无法使用时返回 None。
    """
    if not isinstance(item, dict):
        warnings.append("第 {} 条不是 JSON 对象，已跳过".format(position))
        return None

    name = item.get("name")
    if not isinstance(name, str) or not name.strip():
        warnings.append("第 {} 条缺少 name 字段，已跳过".format(position))
        return None
    name = name.strip()

    value = item.get("value", "")
    if value is None:
        value = ""
    if not isinstance(value, str):
        value = str(value)

    domain = item.get("domain")
    if isinstance(domain, str) and domain.strip():
        domain = domain.strip()
    else:
        domain = default_domain

    path = item.get("path")
    if isinstance(path, str) and path.strip():
        path = path.strip()
        if not path.startswith("/"):
            path = "/" + path
    else:
        path = "/"

    cookie: Dict[str, Any] = {
        "name": name,
        "value": value,
        "domain": domain,
        "path": path,
    }

    # expires / expirationDate -> expires（float 转 int）
    raw_expires = item.get("expires", item.get("expirationDate"))
    if raw_expires is not None and raw_expires != "":
        try:
            expires = int(float(raw_expires))
        except (TypeError, ValueError):
            warnings.append("第 {} 条 expires 字段非法，已忽略".format(position))
            expires = None
        if expires is not None and expires > 0:
            cookie["expires"] = expires

    # 布尔字段透传
    for key in ("httpOnly", "secure"):
        if key in item and item[key] is not None:
            cookie[key] = bool(item[key])

    # sameSite 映射
    raw_same_site = item.get("sameSite", item.get("same_site"))
    if raw_same_site is not None and str(raw_same_site).strip():
        key = str(raw_same_site).strip().lower()
        if key not in SAME_SITE_MAP:
            warnings.append(
                "第 {} 条 sameSite 取值无法识别（{}），已忽略".format(position, key)
            )
        else:
            mapped = SAME_SITE_MAP[key]
            if mapped:
                cookie["sameSite"] = mapped

    return cookie


def _parse_json(raw: str, default_domain: str, warnings: List[str]) -> List[Dict[str, Any]]:
    """解析 JSON 格式的 Cookie 文本。

    Args:
        raw: JSON 文本（数组、单个对象，或 {"cookies": [...]} 包装）。
        default_domain: 缺省 domain。
        warnings: 用于收集跳过原因的列表。

    Returns:
        Cookie 字典列表。

    Raises:
        CookieParseError: JSON 非法或结构不受支持。
    """
    try:
        data = json.loads(raw)
    except ValueError as exc:
        raise CookieParseError("JSON 格式错误: {}".format(exc))

    if isinstance(data, dict):
        inner = data.get("cookies")
        if isinstance(inner, list):
            items = inner
        elif "name" in data:
            items = [data]
        else:
            raise CookieParseError("无法识别的 Cookie 格式，支持分号格式和 JSON 格式")
    elif isinstance(data, list):
        if data and all(isinstance(entry, str) for entry in data):
            # 形如 ["a=1; b=2", "c=3"] 的字符串数组
            cookies: List[Dict[str, Any]] = []
            for entry in data:
                cookies.extend(_parse_semicolon(entry, default_domain, warnings))
            return cookies
        items = data
    else:
        raise CookieParseError("无法识别的 Cookie 格式，支持分号格式和 JSON 格式")

    cookies = []
    for position, item in enumerate(items, start=1):
        cookie = _normalize_item(item, default_domain, position, warnings)
        if cookie is not None:
            cookies.append(cookie)
    return cookies


def parse_cookie_text_detailed(raw: str, site: str) -> Tuple[List[Dict[str, Any]], List[str]]:
    """解析 Cookie 文本，并返回解析过程中的警告信息。

    Args:
        raw: 用户粘贴的 Cookie 文本。
        site: 用户输入的网站域名（可为 URL，内部会规范化）。

    Returns:
        (Cookie 列表, 警告信息列表)。

    Raises:
        CookieParseError: 格式无法识别，或没有任何有效 Cookie。
    """
    normalized_site = normalize_site(site)
    default_domain = cookie_domain(normalized_site)
    warnings: List[str] = []

    fmt = detect_format(raw)
    if fmt == "json":
        cookies = _parse_json(str(raw).strip(), default_domain, warnings)
    else:
        cookies = _parse_semicolon(str(raw).strip(), default_domain, warnings)

    # 去掉完全重复的条目（同名同 domain 同 path）
    unique: List[Dict[str, Any]] = []
    seen = set()
    for cookie in cookies:
        key = (cookie.get("name"), cookie.get("domain"), cookie.get("path"))
        if key in seen:
            continue
        seen.add(key)
        unique.append(cookie)

    if not unique:
        raise CookieParseError("未解析到任何有效 Cookie，请检查粘贴的内容")
    return unique, warnings


def parse_cookie_text(raw: str, site: str) -> List[Dict[str, Any]]:
    """解析 Cookie 文本（简化接口，丢弃警告信息）。

    Args:
        raw: 用户粘贴的 Cookie 文本。
        site: 用户输入的网站域名。

    Returns:
        Cookie 字典列表。

    Raises:
        CookieParseError: 解析失败。
    """
    cookies, _ = parse_cookie_text_detailed(raw, site)
    return cookies


def to_playwright_cookies(cookies: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """把内部 Cookie 结构清洗为可直接交给 Playwright 的结构。

    处理规则：
    - 只保留 Playwright 认识的字段；
    - path 必须存在并以 ``/`` 开头；
    - ``sameSite`` 为 ``None`` 时 Chromium 要求 ``secure`` 为真，否则丢弃该字段；
    - expires 小于等于 0 视为会话 Cookie，直接省略。

    Args:
        cookies: 内部 Cookie 列表。

    Returns:
        清洗后的 Cookie 列表。
    """
    result: List[Dict[str, Any]] = []
    for cookie in cookies:
        if not isinstance(cookie, dict):
            continue
        name = cookie.get("name")
        if not isinstance(name, str) or not name:
            continue

        item: Dict[str, Any] = {
            "name": name,
            "value": "" if cookie.get("value") is None else str(cookie.get("value")),
        }

        domain = cookie.get("domain")
        path = cookie.get("path") or "/"
        if isinstance(domain, str) and domain.strip():
            item["domain"] = domain.strip()
            item["path"] = path if isinstance(path, str) and path.startswith("/") else "/"
        else:
            # 没有 domain 时 Playwright 需要 url 字段
            continue

        expires = cookie.get("expires")
        if isinstance(expires, (int, float)) and not isinstance(expires, bool) and expires > 0:
            item["expires"] = int(expires)

        for key in ("httpOnly", "secure"):
            if key in cookie:
                item[key] = bool(cookie[key])

        same_site = cookie.get("sameSite")
        if same_site in ("Strict", "Lax"):
            item["sameSite"] = same_site
        elif same_site == "None":
            # Chromium 要求 SameSite=None 必须配合 Secure
            if item.get("secure"):
                item["sameSite"] = "None"

        result.append(item)
    return result


def summarize_cookies(cookies: List[Dict[str, Any]]) -> str:
    """生成 Cookie 名称列表摘要（不包含值），用于控制台展示。

    Args:
        cookies: Cookie 列表。

    Returns:
        形如 ``SESSDATA, bili_jct, DedeUserID`` 的字符串。
    """
    names = [str(item.get("name", "")) for item in cookies if isinstance(item, dict)]
    names = [name for name in names if name]
    if not names:
        return "（无）"
    return ", ".join(names)
