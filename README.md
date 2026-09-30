# CookieLuncher（通用 Cookie 启动器）

一个中文控制台程序：启动时校验密码，密码验证成功后进入主菜单，由用户手动选择「运行模式」「修改模式」或「导出 Cookie」。

- **运行模式**：选择已配置的网站，把 Cookie 注入浏览器并打开首页，浏览器关闭后自动清除 Cookie 与临时数据
- **修改模式**：添加 / 查看 / 删除网站 Cookie、修改密码
- **导出 Cookie**：生成 GitHub Action 保活项目可直接使用的 `action_config_output.json`（仅含密文）与 Fernet 密钥

浏览器采用**双后端**（`config.json` 的 `browser_backend` 切换）：

- **`thorium`（默认）**：使用项目内嵌的社区编译 Thorium（win64，自带 H.264/AAC 专有编解码），离线自包含，目标机无需预装任何浏览器；
- **`system`**：探测本机 Edge / Chrome 并复用其浏览器与登录态，用于 DRM 会员视频等场景，非离线模式。

> 项目采用**方案C（折中方案）**：Playwright 仅用于启动 persistent_context 并执行
> `add_cookies`，把 Cookie 交给 Chromium 原生加密持久化写入磁盘 profile（登录态可靠）；
> 页面加载完成后退出 Playwright 上下文，随后由 `subprocess.Popen` 用同一 profile
> 直接启动浏览器独立运行（断开 CDP，不依赖 Playwright），由用户正常关闭。
> 不做 JS 修补 / click 拦截（方案A 已否决），不手写 SQLite / DPAPI 加密（方案B 已否决）。

Cookie 以 Fernet 对称加密保存在单文件 `cookies.enc` 中，密钥由「密码 + 随机盐」经 PBKDF2-HMAC-SHA256（200000 次迭代）派生，明文永远不落盘。

---

## 一、环境要求

- Python 3.10 或更高版本
- 首次安装依赖需要联网（`pip install -r requirements.txt`）
- 源码运行时，`thorium` 后端需要 `external_browsers/thorium-win64/thorium.exe` 已就位（`system` 后端不需要，但本机需装有 Edge 或 Chrome）

## 二、安装

```powershell
pip install -r requirements.txt
```

Thorium（可选，仅 `thorium` 后端需要）：

- 从社区项目 `github.com/gz83/thorium` 的 Releases 下载 Thorium win64 构建（如 `Thorium_SSE4_154.0.8037.45.zip`）
- 解压后把其中 BIN 目录的内容放到 `external_browsers\thorium-win64\`，确保存在 `thorium.exe` 与其版本资源目录（如 `154.0.8037.45`，内含 chrome.dll、Locales、resources 等）

> 源码直接运行时若只用 `system` 后端（`browser_backend="system"`），可跳过 Thorium 放置。

## 三、运行

```powershell
python main.py
```

### 3.1 首次运行

1. 输入并确认新密码（至少 6 位）——这个密码既用于登录程序，也用于加密 Cookie，忘记了无法找回 `cookies.enc` 的内容
2. 程序自动生成 `config.json`（含 Argon2 密码哈希、随机盐）和空的 `cookies.enc`

### 3.2 主菜单

密码验证成功后，进入主菜单：

```
========== Cookie 启动器 ==========
  1. 启动浏览器（加载 Cookie 打开网站）
  2. 进入修改模式（Cookie 管理）
  3. 导出 Cookie（GitHub Action 保活配置）
  0. 退出程序
请选择 [0-3]:
```

- **选项 1**：进入运行模式，选择网站启动浏览器（见 3.4）
- **选项 2**：进入修改模式管理 Cookie（见 3.3）
- **选项 3**：导出 Cookie（见「七、导出 Cookie」）
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
  - **来源 2（浏览器手动登录并采集）**：输入登录 URL（如 `https://www.bilibili.com/xxx`）→ 程序自动识别域名（允许手动修改）→ 弹出**可见浏览器**（headless=False；统一使用内嵌 Thorium / system 后端本机 Edge，**不使用 Playwright 自带 Chromium**）→ 用户手动登录账号/验证码 → 登录完成后**回到控制台按回车确认**（不做页面自动检测）→ 程序读取 Cookie（**全部保留，不做域名过滤**）、显示条数 → 覆盖确认后写入 `cookies.enc`。采集使用**独立临时 user-data-dir**（`cookie_capture_` 前缀，与运行模式的 `cookie_launcher_` 隔离），结束后强制关闭浏览器上下文、按 user-data-dir 查杀残留进程并删除临时目录；采集失败或返回空列表时**拒绝保存**，绝不写入 `cookies.enc`
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
  "second_password_hash": "",
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
| `second_password_hash` | 二重导出密码的 Argon2 哈希（首次使用导出功能时自动写入，不要手改） |
| `log_level` | 写日志的级别，可选 `DEBUG` / `INFO` / `WARNING` / `ERROR` / `CRITICAL` |
| `log_retention_days` | 日志保留天数，启动时删除更早的日志文件 |
| `browser_backend` | `thorium`（默认，使用项目内嵌 Thorium，离线自包含）或 `system`（探测本机 Edge/Chrome，用于 DRM 会员视频，非离线） |
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
CookieLuncher/
├── main.py                  # 程序入口
├── export_for_action.py     # 命令行导出脚本（可选，与主菜单 3 共用同一套逻辑）
├── config.json              # 配置文件（首次运行生成）
├── cookies.enc              # 加密后的 Cookie 存储（所有网站单文件）
├── action_config_output.json # 导出功能生成（仅密文，可提交 GitHub 仓库）
├── logs/
│   └── 2026-09-30.log       # 按日期分文件
├── external_browsers/
│   └── thorium-win64/       # 内嵌 Thorium（thorium.exe + 版本资源目录）
├── core/
│   ├── __init__.py
│   ├── config.py            # 配置读写
│   ├── crypto.py            # 加密/解密/密码派生/哈希
│   ├── cookie_parser.py     # Cookie 格式解析
│   ├── cookie_capture.py    # 浏览器手动登录采集（复用 FIXED_LAUNCH_ARGS）
│   ├── storage.py           # cookies.enc 读写
│   ├── exporter.py          # 导出 GitHub Action 保活配置（UI 与 CLI 共用）
│   ├── logger.py            # 日志（含脱敏）
│   ├── browser.py           # 浏览器双后端启动/注入/清理
│   └── ui.py                # 控制台菜单交互
├── requirements.txt
└── README.md
```

日志格式：

```
2026-09-30 10:23:45 [INFO] 程序启动
2026-09-30 10:23:50 [INFO] 密码验证成功
2026-09-30 10:24:00 [INFO] 用户选择网站: bilibili.com
2026-09-30 10:24:05 [INFO] 已用 Playwright 注入 Cookie: 5 条
2026-09-30 10:24:08 [INFO] 页面加载完成，Cookie 已落盘: https://bilibili.com
2026-09-30 10:24:10 [INFO] 浏览器已启动
2026-09-30 10:30:00 [INFO] 浏览器已关闭
2026-09-30 10:30:01 [INFO] 临时目录已清理
2026-09-30 10:30:02 [INFO] 程序退出
```

**脱敏规则**：日志（含控制台输出）中，字段 `SESSDATA`、`bili_jct`、`token`、`session`、`auth`、`password`、`cookie`、`secret` 的值保留前 6 位，其余替换为 `***`；长度不超过 6 位的值全部隐藏为 `***`。

## 七、导出 Cookie（GitHub Action 保活配置）

在主菜单选择 3，或直接运行命令行脚本 `python export_for_action.py`（两者共用 `core/exporter.py` 的同一套逻辑）。

### 7.1 主菜单 3（带二重密码门禁）

1. **二重导出密码（懒创建）**：首次使用导出功能时，引导设置二重导出密码（至少 6 位，建议与访问密码不同；仅以 Argon2 哈希保存在 `config.json` 的 `second_password_hash`，明文不落盘）；之后每次导出前都必须输入该密码，错误可重试
2. 校验通过后，解密 `cookies.enc`（只读，不修改存储）
3. 对每个站点用本地派生密钥（`derive_key(password, encryption_salt)`）加密「该站点 Cookie 数组的 JSON 文本」，组装并写出 `action_config_output.json`（仅密文）
4. 控制台打印 **Fernet 密钥**（base64-urlsafe），提示存入 GitHub Secret（建议名称 `COOKIE_FERNET_KEY`）

### 7.2 命令行脚本（不要求二重密码）

```powershell
python export_for_action.py
```

流程：读取 `config.json` → 检查首次运行状态与必要字段 → `getpass` 安全读取解锁密码（不回显）→ Argon2 校验（密码错误直接退出）→ 派生密钥并解密 `cookies.enc` → 写出 `action_config_output.json` → 打印 Fernet 密钥与提示。

### 7.3 输出结构

`action_config_output.json` 生成在程序根目录（已存在则直接覆盖），结构如下：

```json
{
  "global": {
    "userAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "timeout": 30000,
    "loginInvalidKeyword": "请登录"
  },
  "cookies": [
    {
      "site": "bilibili.com",
      "fernet_token": "gAAAAAB......"
    }
  ]
}
```

- `global`：固定请求参数（`userAgent` / `timeout` / `loginInvalidKeyword`），GitHub Action 保活侧直接使用
- `cookies[].site`：站点域名（即 `cookies.enc` 存储的键）
- `cookies[].fernet_token`：该站点 Cookie 数组 JSON 的 Fernet 密文，用上面打印的密钥解密
- GitHub Action 侧会自动根据 `site` 拼接访问地址：`https://{site}`（无需 `targetUrl` / `signUrl`）

### 7.4 安全约束

- 明文 Cookie 只存在于导出过程的内存中，绝不打印、绝不写入输出文件
- `encryption_salt` 仅用于本地导出过程中的密钥派生，禁止写入输出文件
- `action_config_output.json` 只包含密文与站点域名，不含明文 Cookie / 盐 / 密码，可以提交到 GitHub 仓库
- **重要警告**：如果修改了访问密码，必须重新执行导出，并**同步更新** GitHub Secret（`COOKIE_FERNET_KEY`）与仓库中的 `action_config_output.json`

## 八、安全说明

- 密码只以 Argon2 哈希形式保存在 `config.json`，程序不保存明文密码
- `cookies.enc` 全程加密，用错误密码无法解密（Fernet 带 HMAC 校验，会直接报「密码错误或数据损坏」）
- 控制台与日志都不打印 Cookie 值
- 每次运行使用独立的临时用户数据目录，浏览器进程树全部退出后删除该目录（Cookie 随目录一并清除）
- 忘记密码 = 无法恢复 `cookies.enc` 中的 Cookie，只能删除 `cookies.enc` 后重新添加

## 九、常见问题

**Q：提示「未找到内置 Thorium 浏览器」**
A：源码运行时报该错误 → 把 Thorium 解压内容放到 `external_browsers\thorium-win64\`（确保有 `thorium.exe` 与版本资源目录）。若使用 `system` 后端则不需要 Thorium。

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

## 十、实现说明（与原始需求明确不同的处理）

1. **空 `cookies.enc` 的生成时机**：设置密码成功后立即生成内容为 `{}` 的加密文件，保证首次运行的验收项成立。
2. **`window_size` 与 `--start-maximized` 的冲突**：`window_size` 为 `maximized`（或无法解析）时使用 `--start-maximized` + `no_viewport=True`；为 `宽x高` 时使用 `--window-size=宽,高` + 固定 `viewport`。
3. **等待浏览器关闭**：阶段2 的浏览器由 `subprocess.Popen` 启动并独立运行，主进程 `proc.wait()` 等待用户关闭窗口；无头模式下不等待，5 秒后自动关闭；Ctrl+C 时 `finally` 应急清理（终止进程树 + 删除目录）。
4. **双后端浏览器定位**：`get_browser_executable(cfg)` 按 `browser_backend` 分发——`thorium` 在源码环境认项目根下的 `external_browsers/thorium-win64/thorium.exe`，找不到就抛出明确错误；`system` 通过注册表 `App Paths`（HKLM+HKCU×Edge/Chrome）再到常见安装路径探测本机浏览器，找不到也抛出明确错误（提示安装浏览器或改回 thorium）。启动一律用 `executable_path` 指向探测/内置结果，不再依赖 Playwright 内置 CFT。
5. **`setup_playwright_env()` 兼容**：`main.py` 启动时会调用一次（现在为清理遗留临时目录 + 保留签名兼容），真正的浏览器路径解析收敛到 `get_browser_executable()`。
6. **B 站 about:blank 跳转修复（方案C 定论）**：**不**使用方案A 的 `window.open` 修补 / `<a target="_blank">` click 拦截（会破坏页面原生跳转交互，禁止使用）；**不**使用方案B 的手写 SQLite / DPAPI / v10 加密预写（外部程序无法生成合法 DPAPI 加密 Blob，会导致 B 站登录失败，禁止使用）。最终方案：Playwright `launch_persistent_context` 绑定临时 profile，`add_cookies` 由 **Chromium 原生加密写盘**（加密 Blob 完全合法，登录态可靠）；页面加载完成后退出 Playwright 上下文；同一 profile 由 `subprocess.Popen` 直接启动浏览器独立运行。`target="_blank"` 保持原生行为，实测不出现 about:blank#blocked。
7. **双后端参数一致**：`thorium` 与 `system` 共用同一组 `FIXED_LAUNCH_ARGS`、同一套 Playwright 注入（`_seed_profile_with_playwright`）与同一套启动命令行组装（`_build_launch_command`），无分支差异；注入阶段统一无头（不闪现窗口，落盘行为与有头一致）。
8. **Playwright 注入细节**：`_to_playwright_cookie()` 把业务 Cookie 转为 `add_cookies` 字段（`expirationDate`→整型 `expires`；无过期时间的会话 Cookie 不传 expires，按会话处理，不落盘）；注入后打开首页确认页面加载完成（`domcontentloaded` + 稳定等待），再 `context.close()` 让 Chromium 正常关闭并完成 Cookie 落盘。
9. **进程树监控与目录清理**：`_wait_process_tree_exit()` 用 psutil 监控主 PID 及全部递归子进程（`children(recursive=True)`），等待整套浏览器进程全部退出后再由 `_remove_dir_retry()` 删除临时 profile 目录（重试规避文件占用）；`cleanup_stale_temp_dirs()` 在程序启动时清理上次异常退出残留的 `cookie_launcher_*` 临时目录。
10. **手动登录采集（core/cookie_capture.py）**：修改模式「添加或替换网站 Cookie」支持「浏览器手动登录并采集」来源——`launch_persistent_context(executable_path=..., headless=False)` 打开登录 URL，用户手动登录后**仅控制台回车确认**结束；`context.cookies()` 读取后按内部结构转换（expires 浮点转整型、会话 Cookie 省略、sameSite 直通），**不做域名过滤**——全部有效 Cookie 一律保留（避免登录态采集不完整），仅剔除无法构成合法条目的数据（缺少 name/value/domain）与完全重复的条目；采集使用**独立临时 user-data-dir**（`cookie_capture_` 前缀，与运行模式的 `cookie_launcher_` 隔离），`context.close()` 完整关闭后由 `finally` 强制 psutil 按 user-data-dir 查杀残留进程并删除临时目录；异常（CDP 错误/启动失败）统一记 ERROR 日志，空列表拒绝保存，绝不写入 `cookies.enc`。启动参数复用 `browser.FIXED_LAUNCH_ARGS`，不复制第二套参数；浏览器路径复用 `browser.get_browser_executable()`（thorium 内嵌 / system 本机探测）。
11. **导出与二重密码（core/exporter.py + core/ui.py）**：导出核心逻辑收敛到 `core/exporter.py`（组装输出结构 + 写文件），UI 菜单与命令行脚本共用；`fernet_token` 内容为「该站点 Cookie 数组的 JSON 文本」的 Fernet 密文，密钥即本地派生密钥；`site` 直接使用 `cookies.enc` 存储的键；输出文件生成在程序根目录（存在直接覆盖）；`global` 固定参数（userAgent / timeout / loginInvalidKeyword）原样写入。主菜单 3 增加二重密码门禁：首次使用懒创建（仅存 `second_password_hash` Argon2 哈希），此后每次导出循环校验，错误可重试；命令行脚本 `export_for_action.py` 不要求二重密码，仅为便捷入口。
