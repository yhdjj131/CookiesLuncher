"""日志模块：按日期分文件、敏感信息脱敏、过期日志清理。

日志文件：logs/YYYY-MM-DD.log
日志格式：``2026-09-13 10:23:45 [INFO] 程序启动``

所有写入日志（含控制台）的内容都会先经过脱敏处理，
对 SESSDATA、bili_jct、token、session、auth、password、cookie、secret
等字段的值保留前 6 位，其余替换为 ``***``。
"""

from __future__ import annotations

import logging
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path
from typing import List, Optional

# 需要脱敏的字段名（大小写不敏感）
SENSITIVE_FIELDS = (
    "sessdata",
    "bili_jct",
    "token",
    "session",
    "auth",
    "password",
    "cookie",
    "secret",
)

# 匹配 "字段名 = 值" / "字段名: 值" / "\"字段名\": \"值\"" 等写法
_SENSITIVE_RE = re.compile(
    r"(?i)(?P<key>\b(?:{keys})\b)(?P<sep>\"?\s*[=:]\s*\"?)(?P<value>[^\s;,&\"']+)".format(
        keys="|".join(SENSITIVE_FIELDS)
    )
)

# 日志文件名：YYYY-MM-DD.log
_LOG_NAME_RE = re.compile(r"^(\d{4})-(\d{2})-(\d{2})\.log$")

# 日志时间格式
LOG_DATE_FORMAT = "%Y-%m-%d %H:%M:%S"

LOGGER_NAME = "cookie_launcher"


def mask_value(value: str, keep: int = 6) -> str:
    """对敏感值脱敏：保留前 keep 位，其余替换为 ``***``。

    Args:
        value: 原始值。
        keep: 保留的前缀长度。

    Returns:
        脱敏后的字符串；长度不足 keep 时全部隐藏。
    """
    text = "" if value is None else str(value)
    if len(text) <= keep:
        return "***"
    return text[:keep] + "***"


def sanitize_text(text: str) -> str:
    """对一整段文本按字段名做脱敏。

    Args:
        text: 原始文本。

    Returns:
        脱敏后的文本。例如 ``SESSDATA=abc123def456`` -> ``SESSDATA=abc123***``。
    """
    if not text:
        return text
    original = str(text)

    def _replace(match: "re.Match[str]") -> str:
        return match.group("key") + match.group("sep") + mask_value(match.group("value"))

    try:
        return _SENSITIVE_RE.sub(_replace, original)
    except Exception:  # pragma: no cover - 正则替换不应失败
        return original


class SanitizingFormatter(logging.Formatter):
    """在输出前对整行日志做脱敏的 Formatter。"""

    def format(self, record: logging.LogRecord) -> str:
        """先按标准流程格式化，再对结果脱敏。"""
        text = super().format(record)
        return sanitize_text(text)


class DailyFileHandler(logging.Handler):
    """按日期自动切换到 ``logs/YYYY-MM-DD.log`` 的文件处理器。"""

    def __init__(self, log_dir: Path, encoding: str = "utf-8") -> None:
        """初始化处理器。

        Args:
            log_dir: 日志目录。
            encoding: 文件编码。
        """
        super().__init__()
        self.log_dir = Path(log_dir)
        self.encoding = encoding
        self._current_date: Optional[str] = None
        self._stream = None

    def _open_stream(self, date_str: str) -> None:
        """按日期打开（或切换）日志文件。"""
        if self._stream is not None:
            try:
                self._stream.close()
            except Exception:
                pass
            self._stream = None
        self.log_dir.mkdir(parents=True, exist_ok=True)
        self._stream = (self.log_dir / "{}.log".format(date_str)).open(
            "a", encoding=self.encoding, newline="\n"
        )
        self._current_date = date_str

    def emit(self, record: logging.LogRecord) -> None:
        """写入一条日志，必要时先切换文件。"""
        try:
            date_str = datetime.fromtimestamp(record.created).strftime("%Y-%m-%d")
            if date_str != self._current_date or self._stream is None:
                self._open_stream(date_str)
            message = self.format(record)
            self._stream.write(message + "\n")
            self._stream.flush()
        except Exception:
            self.handleError(record)

    def close(self) -> None:
        """关闭文件句柄。"""
        try:
            if self._stream is not None:
                self._stream.close()
        finally:
            self._stream = None
            super().close()


def setup_logger(
    log_dir: Path,
    level: str = "INFO",
    console_level: str = "WARNING",
) -> logging.Logger:
    """创建（或重建）全局日志记录器。

    Args:
        log_dir: 日志目录。
        level: 写入文件的日志级别。
        console_level: 输出到控制台的日志级别（默认只输出警告及以上，避免刷屏）。

    Returns:
        配置完成的 Logger。
    """
    logger = logging.getLogger(LOGGER_NAME)
    logger.setLevel(logging.DEBUG)
    logger.propagate = False

    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        try:
            handler.close()
        except Exception:
            pass

    formatter = SanitizingFormatter(fmt="%(asctime)s [%(levelname)s] %(message)s", datefmt=LOG_DATE_FORMAT)

    file_handler = DailyFileHandler(log_dir)
    file_handler.setLevel(getattr(logging, str(level).upper(), logging.INFO))
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)

    # 无控制台环境（例如 PyInstaller --noconsole 打包、stdout/stderr 被重定向为 None）下
    # 不能添加 StreamHandler，否则每条日志都会在 emit 内抛 AttributeError。
    if sys.stderr is not None:
        console_handler = logging.StreamHandler(stream=sys.stderr)
        console_handler.setLevel(getattr(logging, str(console_level).upper(), logging.WARNING))
        console_handler.setFormatter(formatter)
        logger.addHandler(console_handler)

    return logger


def cleanup_old_logs(log_dir: Path, retention_days: int, logger: Optional[logging.Logger] = None) -> int:
    """删除超过保留天数的日志文件。

    Args:
        log_dir: 日志目录。
        retention_days: 保留天数（小于等于 0 时不清理）。
        logger: 可选，用于记录清理结果。

    Returns:
        实际删除的文件数量。
    """
    directory = Path(log_dir)
    if retention_days <= 0 or not directory.exists():
        return 0

    deadline = datetime.now() - timedelta(days=retention_days)
    removed = 0
    for path in directory.glob("*.log"):
        match = _LOG_NAME_RE.match(path.name)
        if not match:
            continue
        try:
            file_date = datetime(int(match.group(1)), int(match.group(2)), int(match.group(3)))
        except ValueError:
            continue
        if file_date >= deadline:
            continue
        try:
            path.unlink()
            removed += 1
        except OSError as exc:
            if logger is not None:
                logger.warning("删除过期日志失败 %s: %s", path, exc)

    if logger is not None and removed:
        logger.info("已清理 %d 个过期日志文件（保留 %d 天）", removed, retention_days)
    return removed


def list_log_files(log_dir: Path) -> List[Path]:
    """列出日志目录中的日志文件，按文件名排序。"""
    directory = Path(log_dir)
    if not directory.exists():
        return []
    return sorted(directory.glob("*.log"))
