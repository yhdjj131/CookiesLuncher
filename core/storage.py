"""cookies.enc 的读写。

存储结构（加密前的明文）：：

    {
      "bilibili.com": [ {cookie}, ... ],
      "zhihu.com": [ {cookie}, ... ]
    }

整个字典用 Fernet 加密后写入单文件 cookies.enc，密钥由密码 + salt 派生。
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any, Dict, List, Tuple

from . import crypto
from .cookie_parser import normalize_site

# 单个网站的最大 Cookie 数量，防止误粘贴超大内容
MAX_COOKIES_PER_SITE = 2000


class StorageError(Exception):
    """Cookie 存储读写失败。"""


class CookieStore:
    """cookies.enc 的读写封装。

    Attributes:
        path: cookies.enc 的路径。
        key: Fernet 密钥。
    """

    def __init__(self, path: Path, key: bytes) -> None:
        """初始化存储对象。

        Args:
            path: cookies.enc 路径。
            key: 由密码与 salt 派生的 Fernet 密钥。
        """
        self.path = Path(path)
        self.key = key

    # ------------------------------------------------------------------ 基础

    def exists(self) -> bool:
        """判断 cookies.enc 是否存在。"""
        return self.path.exists()

    def ensure_file(self) -> bool:
        """文件不存在时创建内容为空（``{}``）的加密文件。

        Returns:
            本次调用是否新建了文件。
        """
        if self.path.exists():
            return False
        self.save({})
        return True

    def load(self) -> Dict[str, List[Dict[str, Any]]]:
        """解密并读取全部网站 Cookie。

        Returns:
            域名 -> Cookie 列表 的字典；文件不存在或为空时返回空字典。

        Raises:
            StorageError: 文件读取失败或内容结构非法。
            crypto.DecryptError: 密钥错误或文件损坏。
        """
        if not self.path.exists():
            return {}

        try:
            raw = self.path.read_bytes()
        except OSError as exc:
            raise StorageError("无法读取 {}: {}".format(self.path, exc))

        if not raw.strip():
            # 空文件视为空存储
            return {}

        data = crypto.decrypt_dict(raw, self.key)
        return self._validate(data)

    def save(self, data: Dict[str, List[Dict[str, Any]]]) -> None:
        """加密并写回 cookies.enc（先写临时文件再替换）。

        Args:
            data: 完整的数据字典。

        Raises:
            StorageError: 写入失败。
        """
        try:
            payload = crypto.encrypt_dict(data, self.key)
        except crypto.CryptoError as exc:
            raise StorageError("加密 Cookie 数据失败: {}".format(exc))

        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp_path = self.path.with_name(self.path.name + ".tmp")
            with tmp_path.open("wb") as handle:
                handle.write(payload)
            os.replace(str(tmp_path), str(self.path))
        except OSError as exc:
            raise StorageError("无法写入 {}: {}".format(self.path, exc))

    # ------------------------------------------------------------------ 业务

    def list_sites(self) -> List[Tuple[str, int]]:
        """列出所有已配置网站及其 Cookie 数量。

        Returns:
            [(域名, Cookie 数量), ...]，按域名排序。
        """
        data = self.load()
        return sorted(((site, len(cookies)) for site, cookies in data.items()), key=lambda x: x[0])

    def get(self, site: str) -> List[Dict[str, Any]]:
        """读取指定网站的 Cookie。

        Args:
            site: 网站域名（会自动规范化）。

        Returns:
            Cookie 列表；网站不存在时返回空列表。
        """
        key = self._safe_key(site)
        data = self.load()
        for stored_site, cookies in data.items():
            if self._safe_key(stored_site) == key:
                return list(cookies)
        return []

    def upsert(self, site: str, cookies: List[Dict[str, Any]]) -> bool:
        """新增或覆盖指定网站的 Cookie。

        Args:
            site: 网站域名（会自动规范化）。
            cookies: Cookie 列表。

        Returns:
            覆盖已有网站返回 True，新增返回 False。

        Raises:
            StorageError: Cookie 数量或结构非法。
        """
        if not isinstance(cookies, list) or not cookies:
            raise StorageError("Cookie 列表不能为空")
        if len(cookies) > MAX_COOKIES_PER_SITE:
            raise StorageError("单个网站的 Cookie 数量超过上限 {} 条".format(MAX_COOKIES_PER_SITE))

        site_key = self._safe_key(site)
        data = self.load()
        overwritten = False
        for stored_site in list(data.keys()):
            if self._safe_key(stored_site) == site_key:
                data.pop(stored_site)
                overwritten = True
        data[site_key] = cookies
        self.save(data)
        return overwritten

    def delete(self, site: str) -> bool:
        """删除指定网站的 Cookie。

        Args:
            site: 网站域名（会自动规范化）。

        Returns:
            实际删除返回 True，网站不存在返回 False。
        """
        site_key = self._safe_key(site)
        data = self.load()
        removed = False
        for stored_site in list(data.keys()):
            if self._safe_key(stored_site) == site_key:
                data.pop(stored_site)
                removed = True
        if removed:
            self.save(data)
        return removed

    # ------------------------------------------------------------------ 内部

    @staticmethod
    def _validate(data: Dict[str, Any]) -> Dict[str, List[Dict[str, Any]]]:
        """校验解密后的数据结构。

        Args:
            data: 解密得到的字典。

        Returns:
            规范化后的字典。

        Raises:
            StorageError: 结构非法。
        """
        if not isinstance(data, dict):
            raise StorageError("Cookie 存储结构非法，应为 JSON 对象")
        result: Dict[str, List[Dict[str, Any]]] = {}
        for site, cookies in data.items():
            if not isinstance(site, str):
                raise StorageError("Cookie 存储中存在非法的域名键")
            if not isinstance(cookies, list):
                raise StorageError("网站 {} 的 Cookie 数据不是列表".format(site))
            cleaned = [item for item in cookies if isinstance(item, dict)]
            result[site] = cleaned
        return result

    @staticmethod
    def _safe_key(site: str) -> str:
        """把用户输入规范化为存储用的键，规范化失败时退化为原始字符串。"""
        try:
            return normalize_site(site)
        except Exception:
            return str(site or "").strip().lower().lstrip(".")
