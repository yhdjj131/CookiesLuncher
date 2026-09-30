# CookiesLuncher（通用 Cookie 启动器）

一个中文控制台程序：启动时校验密码，密码验证成功后进入主菜单，由用户手动选择进入「运行模式」或「修改模式」。

- **修改模式**：添加 / 查看 / 删除网站 Cookie、修改密码
- **运行模式**：选择已配置的网站，把 Cookie 注入浏览器并打开首页，浏览器关闭后自动清除 Cookie 与临时数据

浏览器采用**双后端**（`config.json` 的 `browser_backend` 切换）：

- **`thorium`（默认）**：打包内置社区编译的 Thorium（win64，自带 H.264/AAC 专有编解码），离线自包含，目标机无需预装任何浏览器；
- **`system`**：探测本机 Edge / Chrome 并复用其浏览器与登录态，用于 DRM 会员视频等场景，非离线模式。

> 项目采用**方案C（折中方案）**：Playwright 仅用于启动 persistent_context 并执行
> `add_cookies`，把 Cookie 交给 Chromium 原生加密持久化写入磁盘 profile（登录态可靠）；
> 页面加载完成后退出 Playwright 上下文，随后由 `subprocess.Popen` 用同一 profile
> 直接启动浏览器独立运行（断开 CDP，不依赖 Playwright），由用户正常关闭。
> 不做 JS 修补 / click 拦截（方案A 已否决），不手写 SQLite / DPAPI 加密（方案B 已否决）。

Cookie 以 Fernet 对称加密保存在单文件 `cookies.enc` 中，密钥由「密码 + 随机盐」经 PBKDF2-HMAC-SHA256（200000 次迭代）派生，明文永远不落盘。

---

## 一、环境要求

- Python 3.10 或更高版本（构建/开发）
- 首次安装依赖需要联网（`pip install -r requirements.txt`）
- 源码运行时需要 `external_browsers/thorium-win64/thorium.exe` 已就位（thorium 后端）
- 目标机器（打包分发后）：无需安装 Python，也无需安装浏览器

## 二、安装

```powershell
# 1. 安装依赖
pip install -r requirements.txt

# 2. 放置 Thorium（thorium 后端必需；本项目不下载 Chrome for Testing）
#    解压 Thorium win64 构建，把其中 BIN 目录的内容放到：
#    external_browsers\thorium-win64\（确保存在 thorium.exe 与其版本资源目录）
```

> 源码直接运行时若只用 `system` 后端（`browser_backend="system"`），可跳过第 2 步，但需要本机装有 Edge 或 Chrome。

## 三、运行

```powershell
python main.py
```

### 3.1 首次运行

1. 输入并确认新密码（至少 6 位）——这个密码既用于登录程序，也用于加密 Cookie，忘记了无法找回 `cookies.enc` 的内容
2. 程序自动生成 `config.json`（含 Argon2 密码哈希、随机盐）和空的 `cookies.enc`

### 3.2 主菜单

密码验证成功后，进入主菜单，**不再需要修改 `config.json` 来切换模式**：

```
========== Cookie 启动器 ==========
  1. 启动浏览器（加载 Cookie 打开网站）
  2. 进入修改模式（Cookie 管理）
  0. 退出程序
请选择 [0-2]:
```

- **选项 1**：进入运行模式，选择网站启动浏览器（见 3.4）
- **选项 2**：进入修改模式管理 Cookie（见 3.3）
- **选项 0**：退出程序

### 3.3 修改模式

在主菜单选择 2 进入修改模式：

```
========== Cookie 管理器 ==========
  1. 添加或替换网站 Cookie
  2. 查看已配置网站
  3. 删除网站 Cookie
  4. 修改密码
  5. 返回主菜单
请选择 [1-5]:
```

- **添加或替换网站 Cookie**：先选择 Cookie 来源：
  ```
  ========== 添加或替换网站 Cookie ==========
    1. 粘贴 JSON/文本
    2. 浏览器手动登录并采集 Cookie
    0. 取消
  请选择 Cookie 来源 [0-2]:
  ```
  - **来源 1（粘贴 JSON/文本）**：输入域名（如 `bilibili.com`，也可直接粘贴 `https://www.bilibili.com/xxx`，程序会自动取域名）→ 粘贴 Cookie → 输入空行结束 → 程序自动识别格式并显示解析条数 → 程序读取现有 Cookie 判断：该网站**已存在**则提示「该网站已存在 Cookie，本次操作将直接替换覆盖」，确认后整体替换；**不存在**则提示「全新添加网站 Cookie」并直接新增
  - **来源 2（浏览器手动登录并采集）**：输入登录 URL（如 `https://www.bilibili.com/xxx`）→ 程序自动识别域名（允许手动修改）→ 弹出**可见浏览器**（headless=False；统一使用内嵌 Thorium / system 后端本机 Edge，**不使用 Playwright 自带 Chromium**）→ 用户手动登录账号/验证码 → 登录完成后**回到控制台按回车确认**（不做页面自动检测）→ 程序读取 Cookie（**全部保留，不做域名过滤**）、显示条数 → 覆盖确认后写入 `cookies.enc`。采集使用**独立临时 user-data-dir**（与运行模式隔离），结束后强制关闭浏览器上下文、查杀残留进程并删除临时目录；采集失败或返回空列表时**拒绝保存**，绝不写入 `cookies.enc`
  - ⚠ **安全提示**：Cookie 属于身份凭证，采集后将加密保存在本机 `cookies.enc`，请妥善保管密码，勿向他人泄露 Cookie 内容
- **查看已配置网站**：列出域名与 Cookie 数量（不显示 Cookie 值）
- **删除网站 Cookie**：选择序号 → 二次确认后删除
- **修改密码**：验证旧密码 → 输入新密码两次 → 用旧密钥解密、用新密钥重新加密 `cookies.enc`，并更新 `config.json` 中的 `password_hash` 与 `encryption_salt`

选择 5 返回主菜单，可继续运行模式或退出。

### 3.4 运行模式

在主菜单选择 1 进入运行模式：

```
========== Cookie 启动器 ==========
已配置网站：
  1. bilibili.com（5 个 Cookie）
  2. zhihu.com（8 个 Cookie）
  0. 退出
请选择要打开的网站 [0-2]:
```

选择后：解密 Cookie → 创建临时用户数据目录 → **阶段1**：Playwright 启动 persistent_context
绑定该目录并 `add_cookies` 注入 Cookie（Chromium 原生持久化落盘，加密 Blob 合法），打开
`https://域名` 确认页面加载完成后退出 Playwright 上下文 → **阶段2**：`subprocess.Popen`
用**同一个 profile** 直接启动浏览器（thorium 内置 / system 本机），浏览器独立运行、
断开 CDP，等待用户关闭 → psutil 监控进程树全部退出 → 删除临时目录。

> `system` 后端启动时会提示：依赖本机浏览器，不是离线自包含；会员/DRM 视频请确保已在对应浏览器登录。

## 四、config.json 字段说明

```json
{
  "password_hash": "",
  "encryption_salt": "",
  "log_level": "INFO",
  "log_retention_days": 30,
  "browser_backend": "thorium",
  "browser": {
    "headless": false,
    "window_size": "maximized"
  }
}
```

| 字段 | 说明 |
| --- | --- |
| `password_hash` | Argon2 哈希（首次运行自动写入，不要手改） |
| `encryption_salt` | base64 编码的随机盐（首次运行自动写入，不要手改） |
| `log_level` | 写日志的级别，可选 `DEBUG` / `INFO` / `WARNING` / `ERROR` / `CRITICAL` |
| `log_retention_days` | 日志保留天数，启动时删除更早的日志文件 |
| `browser_backend` | `thorium`（默认，打包内置 Thorium，离线自包含）或 `system`（探测本机 Edge/Chrome，用于 DRM 会员视频，非离线） |
| `browser.headless` | 是否无头模式（无头模式下不等待用户关闭浏览器，5 秒后自动退出） |
| `browser.window_size` | `maximized`（最大化）或 `宽x高`，如 `1280x720` |

> 注意：`password_hash` 与 `encryption_salt` 必须成对保留。改动其中之一会导致 `cookies.enc` 无法解密。

## 五、支持的 Cookie 格式

程序自动识别以下三种格式，均会把缺失的 `domain` 用你输入的域名补全（补成 `.bilibili.com`），`path` 默认为 `/`。

**格式 1：分号分隔字符串**

```
SESSDATA=abc123; bili_jct=def456; DedeUserID=789
```

**格式 2：标准 JSON 数组**

```json
[
  {"name": "SESSDATA", "value": "abc", "domain": ".bilibili.com", "path": "/"}
]
```

**格式 3：Cookie-Editor 导出的 JSON**

```json
[
  {
    "domain": ".bilibili.com",
    "expirationDate": 1234567890.123,
    "hostOnly": false,
    "httpOnly": true,
    "name": "SESSDATA",
    "path": "/",
    "sameSite": "no_restriction",
    "secure": true,
    "session": false,
    "storeId": "0",
    "value": "abc"
  }
]
```

字段映射：`expirationDate → expires`（float 转 int）；`sameSite`：`no_restriction → None`、`lax → Lax`、`strict → Strict`、`unspecified → 不设置`；`hostOnly` / `session` / `storeId` 忽略。若 `sameSite` 为 `None` 但 `secure` 不为真，注入时会自动去掉 `sameSite`（Chromium 的硬性要求）。

## 六、文件与日志

运行后项目目录结构：

```
bili_cookie_launcher/
├── main.py                  # 入口
├── config.json              # 配置文件（首次运行生成）
├── cookies.enc              # 加密后的 Cookie 存储（所有网站单文件）
├── logs/
│   └── 2026-09-13.log       # 按日期分文件
├── external_browsers/
│   └── thorium-win64/       # 内置 Thorium（thorium.exe + 版本资源目录）
├── core/
│   ├── __init__.py
│   ├── config.py            # 配置读写
│   ├── crypto.py            # 加密/解密/密码派生
│   ├── cookie_parser.py     # Cookie 格式解析
│   ├── cookie_capture.py    # 浏览器手动登录采集（复用 FIXED_LAUNCH_ARGS）
│   ├── storage.py           # cookies.enc 读写
│   ├── logger.py            # 日志（含脱敏）
│   ├── browser.py           # 浏览器双后端启动/注入/清理
│   └── ui.py                # 控制台菜单交互
├── build.bat                # 打包脚本
├── requirements.txt
└── README.md
```

日志格式：

```
2026-09-13 10:23:45 [INFO] 程序启动
2026-09-13 10:23:50 [INFO] 密码验证成功
2026-09-13 10:24:00 [INFO] 用户选择网站: bilibili.com
2026-09-13 10:24:05 [INFO] 已用 Playwright 注入 Cookie: 5 条
2026-09-13 10:24:08 [INFO] 页面加载完成，Cookie 已落盘: https://bilibili.com
2026-09-13 10:24:10 [INFO] 浏览器已启动
2026-09-13 10:30:00 [INFO] 浏览器已关闭
2026-09-13 10:30:01 [INFO] 临时目录已清理
2026-09-13 10:30:02 [INFO] 程序退出
```

**脱敏规则**：日志（含控制台输出）中，字段 `SESSDATA`、`bili_jct`、`token`、`session`、`auth`、`password`、`cookie`、`secret` 的值保留前 6 位，其余替换为 `***`；长度不超过 6 位的值全部隐藏为 `***`。

## 七、打包（生成完全自包含的 dist）

打包的目标是产出一个**完全自包含**的 `dist/CookieLauncher/`：内嵌 Thorium，目标机器不需要 Python、Node.js、Playwright、浏览器，也不需要联网。

```powershell
# 第 1 步：确认 Thorium 已放在 external_browsers\thorium-win64\（只需执行一次）
.\prepare_browsers.bat

# 第 2 步：打包（校验 Thorium 存在 → PyInstaller → 校验 → 自检）
.\build.bat

# 第 3 步：自检（build.bat 结尾会自动执行，也可手动运行）
python verify_selfcontained.py
```

本项目**不下载、不打包、不保留** Playwright 内置的 Chrome for Testing：`build.bat` 通过
`--add-data "external_browsers;external_browsers"` 把 Thorium 打进 `_internal/external_browsers/`，
运行时由 `core/browser.py` 用 `executable_path` 指向它。

`build.bat` 的六个步骤（全程 ASCII 英文、失败即停、不静默继续）：

| 步骤 | 作用 |
| --- | --- |
| 0 | 检查 Python 与 PyInstaller |
| 1 | 校验 `external_browsers\thorium-win64\thorium.exe` 存在（缺失则报错，提示放置） |
| 2 | 清理旧的 `build/`、`dist/`、`.spec` |
| 3 | 运行 PyInstaller（`--collect-all playwright` + `--add-data` 收集 Thorium） |
| 4 | 复制 VC++ 运行库 DLL 到 `_internal`，以及 `thorium.exe` / `node.exe` 旁边 |
| 5 | 校验 `_internal\external_browsers\thorium-win64\thorium.exe` 真实存在，否则报错退出 |
| 6 | 写入 `config.json` 模板（含 `browser_backend`）并运行 `verify_selfcontained.py` |

产物：

```
dist/CookieLauncher/
├── CookieLauncher.exe
├── config.json              # 模板（含 browser_backend="thorium"；用户可编辑）
├── _internal/               # Python + 依赖 + 内嵌 Thorium
│   └── external_browsers/thorium-win64/thorium.exe（+ 版本资源目录）
└── （首次运行生成 cookies.enc、logs/）
```

> 分发前请确认 `config.json` 中没有真实的 `password_hash` 与 `encryption_salt`，否则相当于把密码哈希一起发出去了。

> `build.bat` / `prepare_browsers.bat` 中的提示信息刻意使用 ASCII 英文：cmd.exe 在 UTF-8 代码页（65001）下解析含中文字符的批处理文件时会出现字节偏移错乱，可能执行到被截断的垃圾命令。程序本身的中文提示不受影响。

## 八、完全无依赖说明

`dist/CookieLauncher/` 文件夹是完全自包含的：

- 已内嵌 Thorium（`thorium` 后端无需目标机器安装任何浏览器；`system` 后端可选本机 Edge/Chrome）
- 已内嵌 Python 解释器与全部依赖
- 已随包复制 VC++ 运行库 DLL（无需目标机器安装 Visual C++ Redistributable）
- 无需 Python、Node.js、Playwright
- 无需联网（Cookie 是本地注入的，只有网站页面本身需要网络）
- **不依赖 `%LOCALAPPDATA%\ms-playwright`**：打包版只认 `sys._MEIPASS` 下内嵌的 Thorium 路径，找不到就直接报错，绝不静默回落到用户目录

### 分发方式

把整个 `dist\CookieLauncher\` 文件夹压缩后发给用户，解压后双击 `CookieLauncher.exe` 即可。

### 首次运行

1. 程序提示设置密码
2. 正常运行，密码验证后进入主菜单
3. 选择 2 进入修改模式，添加网站 Cookie
4. 返回主菜单选择 1，选择网站，浏览器打开

### 体积说明

由于内嵌了 Thorium（解压后约 700 MB+），文件夹约 750-1000 MB，这是正常的。Thorium 是社区第三方编译版（来源：`github.com/gz83/thorium`，本项目实测使用 M154.0.8037.45 的 SSE4 构建；内核版本会随上游迭代），自带 H.264/AAC 专有编解码，因此**普通 B 站视频可直接播放**。

> 关于 Widevine：本项目**不实现、不打包、不分发** Widevine DRM 组件。Thorium 目录里的 WidevineCdm 是构建自带的，仅供 `system` 后端复用本机浏览器时由系统处理；DRM 会员视频请使用 `system` 后端（本机 Edge/Chrome）。

### 关于 VC++ 运行库

实测本包内 `thorium.exe`、`chrome.dll`、`node.exe`、`CookieLauncher.exe` 的导入表都**不**包含
`msvcp140.dll` / `vcruntime140.dll`（Chromium/Thorium 与 Node 在 Windows 上静态链接了 CRT），
唯一需要 `VCRUNTIME140.dll` 的是 `python38.dll`，PyInstaller 已自动收集。
`build.bat` 仍会把三个 DLL 复制到 `_internal` 以及 `thorium.exe`、`node.exe` 同级目录，
作为对旧版运行库/其他环境的保险；若在打包机上找不到这些 DLL，脚本会打印警告。

## 九、安全说明

- 密码只以 Argon2 哈希形式保存在 `config.json`，程序不保存明文密码
- `cookies.enc` 全程加密，用错误密码无法解密（Fernet 带 HMAC 校验，会直接报「密码错误或数据损坏」）
- 控制台与日志都不打印 Cookie 值
- 每次运行使用独立的临时用户数据目录，浏览器进程树全部退出后删除该目录（Cookie 随目录一并清除）
- 忘记密码 = 无法恢复 `cookies.enc` 中的 Cookie，只能删除 `cookies.enc` 后重新添加

## 十、常见问题

**Q：提示「浏览器启动失败，请检查浏览器是否完整」或「未找到内置 Thorium 浏览器」**
A：源码运行时报「未找到内置 Thorium 浏览器」→ 把 Thorium 解压内容放到 `external_browsers\thorium-win64\`（确保有 `thorium.exe` 与版本资源目录）。打包版报错 → 说明打包时没把 Thorium 打进 `_internal`：先运行 `prepare_browsers.bat`，再运行 `build.bat`，最后用 `python verify_selfcontained.py` 确认。

**Q：提示「未检测到本机 Edge/Chrome」**
A：`browser_backend` 为 `system` 但本机未安装 Edge/Chrome，或注册表/安装路径探测不到。请安装浏览器，或把 `browser_backend` 改回 `thorium`。

**Q：B 站视频无法播放 / 提示浏览器版本过低**
A：确保 `browser_backend` 为 `thorium`（内置构建自带专有编解码）。若使用 `system` 后端，播放能力取决于本机浏览器版本与登录态。

**Q：提示「无法识别的 Cookie 格式，支持分号格式和 JSON 格式」**
A：粘贴内容中至少需要包含 `=`（分号格式）或以 `[` `{` 开头的 JSON。注意不要粘贴 `Cookie:` 前缀。

**Q：如何进入修改模式添加 Cookie？**
A：密码验证成功后，在主菜单选择 2「进入修改模式」即可，无需再修改 `config.json`。旧版 `config.json` 中的 `modify_mode` 标记已废弃，程序启动时会自动忽略并移除。

**Q：日志里 Cookie 值显示不全**
A：这是预期行为，属于脱敏处理。长度不超过 6 位的值会整体显示为 `***`。

**Q：Ctrl+C 会怎样？**
A：捕获 `KeyboardInterrupt` 后会输出「已中断，程序退出」；若正在运行浏览器，临时目录会在 `finally` 中尽力清理。

## 十一、实现说明（与原始需求明确不同的处理）

1. **空 `cookies.enc` 的生成时机**：设置密码成功后立即生成内容为 `{}` 的加密文件，保证首次运行的验收项成立。
2. **`window_size` 与 `--start-maximized` 的冲突**：`window_size` 为 `maximized`（或无法解析）时使用 `--start-maximized` + `no_viewport=True`；为 `宽x高` 时使用 `--window-size=宽,高` + 固定 `viewport`。
3. **等待浏览器关闭**：阶段2 的浏览器由 `subprocess.Popen` 启动并独立运行，主进程 `proc.wait()` 等待用户关闭窗口；无头模式下不等待，5 秒后自动关闭；Ctrl+C 时 `finally` 应急清理（终止进程树 + 删除目录）。
4. **双后端浏览器定位**：`get_browser_executable(cfg)` 按 `browser_backend` 分发——`thorium` 在打包环境（存在 `sys._MEIPASS`）下只认 `_MEIPASS/external_browsers/thorium-win64/thorium.exe`，源码环境认项目根下的同名路径，找不到就抛出明确错误；`system` 通过注册表 `App Paths`（HKLM+HKCU×Edge/Chrome）再到常见安装路径探测本机浏览器，找不到也抛出明确错误（提示安装浏览器或改回 thorium）。启动一律用 `executable_path` 指向探测/内置结果，不再依赖 Playwright 内置 CFT。
5. **`setup_playwright_env()` 兼容**：`main.py` 启动时会调用一次（现在为清理遗留临时目录 + 保留签名兼容），真正的浏览器路径解析收敛到 `get_browser_executable()`。
6. **B 站 about:blank 跳转修复（方案C 定论）**：**不**使用方案A 的 `window.open` 修补 / `<a target="_blank">` click 拦截（会破坏页面原生跳转交互，禁止使用）；**不**使用方案B 的手写 SQLite / DPAPI / v10 加密预写（外部程序无法生成合法 DPAPI 加密 Blob，会导致 B 站登录失败，禁止使用）。最终方案：Playwright `launch_persistent_context` 绑定临时 profile，`add_cookies` 由 **Chromium 原生加密写盘**（加密 Blob 完全合法，登录态可靠）；页面加载完成后退出 Playwright 上下文；同一 profile 由 `subprocess.Popen` 直接启动浏览器独立运行。`target="_blank"` 保持原生行为，实测不出现 about:blank#blocked。
7. **双后端参数一致**：`thorium` 与 `system` 共用同一组 `FIXED_LAUNCH_ARGS`、同一套 Playwright 注入（`_seed_profile_with_playwright`）与同一套启动命令行组装（`_build_launch_command`），无分支差异；注入阶段统一无头（不闪现窗口，落盘行为与有头一致）。
8. **Playwright 注入细节**：`_to_playwright_cookie()` 把业务 Cookie 转为 `add_cookies` 字段（`expirationDate`→整型 `expires`；无过期时间的会话 Cookie 不传 expires，按会话处理，不落盘）；注入后打开首页确认页面加载完成（`domcontentloaded` + 稳定等待），再 `context.close()` 让 Chromium 正常关闭并完成 Cookie 落盘。
9. **进程树监控与目录清理**：`_wait_process_tree_exit()` 用 psutil 监控主 PID 及全部递归子进程（`children(recursive=True)`），等待整套浏览器进程全部退出后再由 `_remove_dir_retry()` 删除临时 profile 目录（重试规避文件占用）；`cleanup_stale_temp_dirs()` 在程序启动时清理上次异常退出残留的 `cookie_launcher_*` 临时目录。
10. **验证（verify_bili.py，双后端实测通过）**：用真实有效 B 站 Cookie（含 SESSDATA）——Playwright 注入落盘后断言持久 Cookie 行数；第二阶段独立浏览器经 CDP 断言 Cookie 值一致、B 站 nav 接口 `isLogin=true`（保持登录）、点击首页视频卡片 `<a target="_blank">` 新标签正常打开视频页（无 about:blank#blocked）、CDP `Browser.close` 模拟用户关闭后进程树全部退出、临时目录完整删除。Playwright 的 `connect_over_cdp().close()` 只 detach 不关闭 Thorium，需用 CDPSession 发送 `Browser.close`。
11. **手动登录采集（core/cookie_capture.py）**：修改模式「添加或替换网站 Cookie」支持「浏览器手动登录并采集」来源——`launch_persistent_context(executable_path=..., headless=False)` 打开登录 URL，用户手动登录后**仅控制台回车确认**结束；`context.cookies()` 读取后按内部结构转换（expires 浮点转整型、会话 Cookie 省略、sameSite 直通），**不做域名过滤**——全部有效 Cookie 一律保留（避免登录态采集不完整），仅剔除无法构成合法条目的数据（缺少 name/value/domain）与完全重复的条目；采集使用**独立临时 user-data-dir**（`cookie_capture_` 前缀，与运行模式的 `cookie_launcher_` 隔离），`context.close()` 完整关闭后由 `finally` 强制 psutil 按 user-data-dir 查杀残留进程并删除临时目录；异常（CDP 错误/启动失败）统一记 ERROR 日志，空列表拒绝保存，绝不写入 `cookies.enc`。启动参数复用 `browser.FIXED_LAUNCH_ARGS`，不复制第二套参数；浏览器路径复用 `browser.get_browser_executable()`（thorium 内嵌 / system 本机探测）。
