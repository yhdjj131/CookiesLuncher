#!/usr/bin/env python
"""verify_selfcontained.py - 验证 dist\\CookieLauncher\\ 是否完全自包含。

检查内容：
1. CookieLauncher.exe 存在
2. 后端=thorium：内嵌 Thorium（thorium.exe）存在，且位于 browser.py 会查找的路径下
   （后端=system 不依赖内置二进制，跳过本项）
3. VC++ 运行库 DLL 已随包分发
4. config.json 模板存在（含 browser_backend 字段）
5. Playwright 驱动（node.exe）存在
6. 总体积合理（内嵌 Thorium 后应明显大于 200MB）

用法::

    python verify_selfcontained.py [dist目录]

退出码：0 = 通过，1 = 未通过。
"""

import json
import sys
from pathlib import Path

# thorium 后端在打包版中的路径（相对 sys._MEIPASS，即 _internal）
THORIUM_RELATIVE_PATH = Path("external_browsers") / "thorium-win64" / "thorium.exe"

VC_DLLS = ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")

MIN_EXPECTED_MB = 200.0
TYPICAL_MIN_MB = 700.0
TYPICAL_MAX_MB = 1100.0


class Report:
    """收集检查结果并打印。"""

    def __init__(self):
        self.failures = []

    def check(self, description, condition, detail=""):
        """记录一项检查结果。"""
        status = "PASS" if condition else "FAIL"
        line = "[{}] {}".format(status, description)
        if detail and not condition:
            line += "  -> {}".format(detail)
        print(line)
        if not condition:
            self.failures.append(description)
        return bool(condition)

    def note(self, message):
        """打印普通提示。"""
        print("       {}".format(message))


def human_size(num_bytes):
    """把字节数格式化为人类可读的字符串。"""
    return "{:.1f} MB".format(num_bytes / 1024.0 / 1024.0)


def find_first(root, filename):
    """在目录树中查找第一个匹配的**文件**（跳过同名目录）。"""
    for path in root.rglob(filename):
        if path.is_file():
            return path
    return None


def main(argv=None):
    """执行全部检查。

    Args:
        argv: 命令行参数（不含程序名），第一个参数可指定 dist 目录。

    Returns:
        进程退出码：0 通过，1 未通过。
    """
    argv = list(sys.argv[1:] if argv is None else argv)
    project_root = Path(__file__).resolve().parent
    dist = Path(argv[0]).resolve() if argv else project_root / "dist" / "CookieLauncher"

    print("=" * 60)
    print("Verifying self-contained package")
    print("dist: {}".format(dist))
    print("=" * 60)

    report = Report()

    # 1. 主程序
    exe = dist / "CookieLauncher.exe"
    report.check("CookieLauncher.exe exists", exe.exists(), exe)
    if not dist.exists():
        print("\nRESULT: FAIL - dist directory not found, run build.bat first")
        return 1

    internal = dist / "_internal"

    # 2. 内嵌 Thorium（thorium 后端必需；system 后端可跳过）
    config_path = dist / "config.json"
    backend = "thorium"
    if config_path.exists():
        try:
            cfg = json.loads(config_path.read_text(encoding="utf-8-sig"))
            backend = str(cfg.get("browser_backend") or "thorium").strip().lower()
        except Exception:
            pass

    if backend == "system":
        report.note("browser_backend=system -> 跳过内置浏览器自检（依赖本机 Edge/Chrome）")
    else:
        thorium = internal / THORIUM_RELATIVE_PATH
        report.check(
            "Thorium embedded (thorium.exe)",
            thorium.is_file(),
            "not found at {}".format(thorium),
        )
        if thorium.is_file():
            report.note("thorium.exe: {}".format(thorium))

    # 3. VC++ 运行库
    for dll in VC_DLLS:
        direct = internal / dll
        if direct.is_file():
            found = [direct]
        else:
            found = [path for path in dist.rglob(dll) if path.is_file()]
        report.check("VC++ dll: {}".format(dll), bool(found), "not found in {}".format(dist))
        if found:
            report.note("{}: {}".format(dll, found[0]))

    # 4. 配置文件模板
    report.check("config.json present", config_path.exists(), config_path)
    if config_path.exists():
        try:
            cfg = json.loads(config_path.read_text(encoding="utf-8-sig"))
            report.note("browser_backend: {}".format(cfg.get("browser_backend", "(missing)")))
        except Exception:
            pass

    # 5. Playwright 驱动
    node = find_first(dist, "node.exe")
    report.check("Playwright driver (node.exe) present", node is not None, "not found under {}".format(dist))

    # 6. 体积
    total = sum(path.stat().st_size for path in dist.rglob("*") if path.is_file())
    size_mb = total / 1024.0 / 1024.0
    print("\nTotal size: {}".format(human_size(total)))
    if size_mb < MIN_EXPECTED_MB:
        print("[WARN] Size < {} MB: Thorium may not be embedded properly".format(MIN_EXPECTED_MB))
    elif size_mb < TYPICAL_MIN_MB or size_mb > TYPICAL_MAX_MB:
        print(
            "[INFO] Size is outside the typical {:.0f}-{:.0f} MB range "
            "(depends on the Thorium build; system-only bundles are much smaller)".format(
                TYPICAL_MIN_MB, TYPICAL_MAX_MB
            )
        )

    print("\n" + "=" * 60)
    if report.failures:
        print("RESULT: FAIL - Package is NOT fully self-contained")
        for item in report.failures:
            print("  - {}".format(item))
        print("=" * 60)
        return 1

    print("RESULT: PASS - Package is self-contained")
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())
