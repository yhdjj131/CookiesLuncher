"""加密、解密与密码哈希。

- 密码哈希：Argon2（argon2-cffi）
- 数据加密：Fernet 对称加密（cryptography）
- 密钥派生：PBKDF2-HMAC-SHA256，迭代 200000 次

第三方库采用延迟导入，缺失时给出中文提示而不是直接抛 ImportError。
"""

from __future__ import annotations

import base64
import hashlib
import json
from typing import Any, Dict

# PBKDF2 迭代次数
PBKDF2_ITERATIONS = 200_000

# 派生密钥长度（字节），Fernet 要求 32 字节并做 base64 urlsafe 编码
KEY_BYTES = 32

_INSTALL_HINT = "缺少依赖 {}，请先执行: pip install -r requirements.txt"


class CryptoError(Exception):
    """加密模块的通用错误。"""


class DecryptError(CryptoError):
    """解密失败（通常是密码错误或数据损坏）。"""


class MissingDependencyError(CryptoError):
    """缺少必要的第三方依赖。"""


def _import_fernet():
    """延迟导入 cryptography 的 Fernet 与异常类型。

    Returns:
        (Fernet, InvalidToken) 元组。

    Raises:
        MissingDependencyError: 未安装 cryptography。
    """
    try:
        from cryptography.fernet import Fernet, InvalidToken
    except ImportError as exc:  # pragma: no cover - 取决于运行环境
        raise MissingDependencyError(_INSTALL_HINT.format("cryptography")) from exc
    return Fernet, InvalidToken


def _import_password_hasher():
    """延迟导入 argon2 的 PasswordHasher。

    Returns:
        PasswordHasher 实例。

    Raises:
        MissingDependencyError: 未安装 argon2-cffi。
    """
    try:
        from argon2 import PasswordHasher
        from argon2.exceptions import VerifyMismatchError  # noqa: F401
    except ImportError as exc:  # pragma: no cover - 取决于运行环境
        raise MissingDependencyError(_INSTALL_HINT.format("argon2-cffi")) from exc
    return PasswordHasher()


def derive_key(password: str, salt_b64: str) -> bytes:
    """由密码和盐值派生 Fernet 密钥。

    Args:
        password: 用户密码（明文）。
        salt_b64: base64 编码的盐值。

    Returns:
        可直接用于 Fernet 的 32 字节密钥（base64 urlsafe 编码）。

    Raises:
        CryptoError: 密码为空或盐值非法。
    """
    if not password:
        raise CryptoError("密码不能为空")
    try:
        salt = base64.b64decode(salt_b64 or "", validate=False)
    except Exception as exc:  # binascii.Error 等
        raise CryptoError("盐值不是合法的 base64 字符串，无法派生密钥") from exc
    if not salt:
        raise CryptoError("盐值为空，无法派生密钥")

    kdf = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, PBKDF2_ITERATIONS)
    return base64.urlsafe_b64encode(kdf)


def hash_password(password: str) -> str:
    """使用 Argon2 生成密码哈希。

    Args:
        password: 用户密码（明文）。

    Returns:
        Argon2 哈希字符串（含参数与盐值）。

    Raises:
        CryptoError: 密码为空。
        MissingDependencyError: 未安装 argon2-cffi。
    """
    if not password:
        raise CryptoError("密码不能为空")
    hasher = _import_password_hasher()
    return hasher.hash(password)


def verify_password(hash_str: str, password: str) -> bool:
    """校验密码是否与 Argon2 哈希匹配。

    Args:
        hash_str: config.json 中保存的 Argon2 哈希。
        password: 用户输入的密码。

    Returns:
        校验通过返回 True，否则返回 False（不抛异常）。
    """
    if not hash_str or not password:
        return False
    try:
        hasher = _import_password_hasher()
    except MissingDependencyError:
        raise
    try:
        hasher.verify(hash_str, password)
        return True
    except Exception:
        return False


def encrypt_dict(data: Dict[str, Any], key: bytes) -> bytes:
    """把字典序列化为 JSON 后加密。

    Args:
        data: 待加密的字典。
        key: derive_key 派生出的密钥。

    Returns:
        Fernet 加密后的字节串。
    """
    Fernet, _ = _import_fernet()
    try:
        payload = json.dumps(data, ensure_ascii=False).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise CryptoError("待加密数据无法序列化为 JSON: {}".format(exc))
    try:
        return Fernet(key).encrypt(payload)
    except Exception as exc:
        raise CryptoError("加密失败: {}".format(exc))


def decrypt_dict(encrypted: bytes, key: bytes) -> Dict[str, Any]:
    """解密字节串并解析为字典。

    Args:
        encrypted: Fernet 密文。
        key: derive_key 派生出的密钥。

    Returns:
        解密后的字典。

    Raises:
        DecryptError: 密钥错误或数据损坏（统一提示「密码错误或数据损坏」）。
    """
    Fernet, InvalidToken = _import_fernet()
    try:
        plain = Fernet(key).decrypt(encrypted)
    except InvalidToken as exc:
        raise DecryptError("密码错误或数据损坏") from exc
    except Exception as exc:
        raise DecryptError("密码错误或数据损坏") from exc

    try:
        data = json.loads(plain.decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise DecryptError("密码错误或数据损坏") from exc

    if not isinstance(data, dict):
        raise DecryptError("密码错误或数据损坏")
    return data


def mask_secret(value: str, keep: int = 6) -> str:
    """对敏感值脱敏：保留前 keep 位，其余用 *** 代替。

    Args:
        value: 原始敏感值。
        keep: 保留的前缀长度。

    Returns:
        脱敏后的字符串。长度不足 keep 时全部隐藏。
    """
    text = "" if value is None else str(value)
    if len(text) <= keep:
        return "***"
    return text[:keep] + "***"
