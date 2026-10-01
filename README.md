# CookieLuncher（Cookie 启动器）

一个本地运行的中文控制台程序：把你的网站登录 Cookie 加密保存在本机，需要时一键注入浏览器打开网站，也可以手动登录采集 Cookie、导出给 GitHub Action 保活项目使用。

---

## 它能做什么

- **加密保存多网站 Cookie**：所有网站 Cookie 加密写入单个 `cookies.enc`，明文永不落盘，密码错误无法解密
- **一键启动浏览器**：选中网站后自动注入 Cookie 并打开首页，登录态可靠，浏览器关闭后自动清除临时数据
- **B 站视频正常播放**：内置 Thorium 浏览器自带 H.264/AAC 专有编解码，普通 B 站视频可直接播放，`target="_blank"` 新标签跳转正常
- **手动登录采集 Cookie**：打开可见浏览器让你登录账号/验证码，回车确认后自动采集全部有效 Cookie
- **Cookie 管理**：粘贴导入（分号 / JSON / Cookie-Editor 格式）、查看、删除、修改密码，重复导入自动提示覆盖
- **导出 GitHub Action 保活配置**：一键生成仅含密文的 `action_config_output.json` 和 Fernet 密钥，直接对接配套的 [Keep-Alive](https://github.com/yhdjj131/Keep-Alive) 保活仓库定时自动保活，带二重密码保护
- **双浏览器后端**：内置 Thorium（离线自包含）或本机 Edge/Chrome（DRM 会员视频），`config.json` 一键切换

---

## 快速开始

### 环境要求

- Windows
- Python 3.10 或更高版本
- 首次安装依赖需要联网

### 安装

```powershell
pip install -r requirements.txt
```

Thorium 浏览器（可选）：默认后端 `thorium` 需要它。从 `github.com/gz83/thorium` 的 Releases 下载 Thorium win64 构建，解压后把 BIN 目录内容放到 `external_browsers\thorium-win64\`（确保存在 `thorium.exe` 与版本资源目录）。如果只用 `system` 后端（本机 Edge/Chrome），可以跳过这一步。

### 运行

```powershell
python main.py
```

首次运行：设置访问密码（至少 6 位）→ 程序生成 `config.json` 和空的 `cookies.enc`。这个密码同时用于登录程序和加密 Cookie，忘记后无法找回 Cookie。

### 主菜单

```
========== Cookie 启动器 ==========
  1. 启动浏览器（加载 Cookie 打开网站）
  2. 进入修改模式（Cookie 管理）
  3. 导出 Cookie（GitHub Action 保活配置）
  0. 退出程序
请选择 [0-3]:
```

---

## 功能详解

### 1. 启动浏览器（运行模式）

选择已配置的网站，程序按以下流程启动浏览器：

1. 创建临时用户数据目录，Playwright 启动浏览器并注入 Cookie（Chromium 原生加密写盘，登录态可靠）
2. 页面加载完成后退出 Playwright，由程序直接拉起同一浏览器独立运行
3. 你正常使用浏览器、关闭窗口后，程序监控到全部进程退出，自动删除临时目录

`system` 后端会提示使用本机浏览器及登录态，会员/DRM 视频请确保已在该浏览器登录。

### 2. 修改模式（Cookie 管理）

```
========== Cookie 管理器 ==========
  1. 添加或替换网站 Cookie
  2. 查看已配置网站
  3. 删除网站 Cookie
  4. 修改密码
  5. 返回主菜单
请选择 [1-5]:
```

**添加或替换网站 Cookie** 提供两种来源：

- **粘贴 JSON/文本**：输入域名（如 `bilibili.com`，或直接粘贴链接自动取域名）→ 粘贴 Cookie 文本（空行结束）→ 自动识别格式并显示条数 → 已存在则提示覆盖、确认后整体替换，不存在则直接新增
- **浏览器手动登录并采集**：输入登录 URL → 自动识别域名（可手动修改）→ 打开可见浏览器手动登录账号/验证码 → 回到控制台按回车确认 → 采集全部有效 Cookie（不做域名过滤，保证登录态完整）→ 覆盖确认后写入
  - 采集使用独立临时目录，结束后自动查杀残留进程并删除；空结果或失败时拒绝保存

**查看 / 删除**：列出网站与 Cookie 数量（不显示值），删除需二次确认。**修改密码**：验证旧密码后用新密钥整体重新加密。

### 3. 导出 Cookie（GitHub Action 保活配置）

两种方式，共用同一套导出逻辑：

- **主菜单 3**：带**二重导出密码**保护——首次使用先设置二重密码（仅存哈希，明文不落盘），之后每次导出必须输入该密码，错误可重试
- **命令行**：`python export_for_action.py`——读取解锁密码（不回显）→ 校验 → 导出（不要求二重密码，适合脚本环境）

导出后：

- 生成 `action_config_output.json`（程序根目录），只包含密文，可以提交到 GitHub 仓库
- 控制台打印 **Fernet 密钥**（base64-urlsafe），提示存入 GitHub Secret（名称 `COOKIE_FERNET_KEY`）

```json
{
  "global": {
    "userAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "timeout": 30000,
    "loginInvalidKeyword": "请登录"
  },
  "cookies": [
    { "site": "bilibili.com", "fernet_token": "gAAAAAB......" }
  ]
}
```

- `global`：Action 侧直接使用的固定请求参数
- `cookies[].site`：站点域名；`cookies[].fernet_token`：该站点 Cookie 的 Fernet 密文（用上面打印的密钥解密）
- Action 侧按 `https://{site}` 自动拼接访问地址

> ⚠ 如果之后修改了访问密码，必须重新导出，并同步更新 GitHub Secret 与仓库中的 `action_config_output.json`。

### 4. 对接 Keep-Alive 保活仓库

导出的 `action_config_output.json` 可直接交给配套的 **Keep-Alive** 保活仓库（https://github.com/yhdjj131/Keep-Alive ），由 GitHub Actions 定时访问你的站点，保持各网站登录会话长期有效。

对接步骤：

1. 在本程序完成「导出 Cookie」，得到 `action_config_output.json` 与控制台打印的 Fernet 密钥
2. 把 `action_config_output.json` 提交到 Keep-Alive 仓库（覆盖同名文件并推送；也可以先 **Fork** 该仓库再配置，详见其 README 的「其他用户 Fork 使用」）
3. 在 Keep-Alive 仓库配置 Secret：**Settings → Secrets and variables → Actions → New repository secret**，Name 填 `COOKIE_FERNET_KEY`，Value 粘贴导出的密钥
4. Keep-Alive 内置定时任务：**每周二、四、六 北京时间 12:00** 自动运行保活（脚本启动后随机延迟 0~7 分钟执行；Fork 的仓库需先在 Actions 页手动 **Enable workflows**）
5. 到 Keep-Alive 的 **Actions** 页面查看任务摘要：总站点数、成功/失败站点及原因一目了然

> 修改访问密码后：重新导出 → 同时更新 Keep-Alive 仓库中的 `action_config_output.json` 与 `COOKIE_FERNET_KEY` Secret，两者必须匹配，否则解密失败、任务全部报错。

---

## 双后端怎么选

| 后端 | 浏览器 | 适用场景 |
| --- | --- | --- |
| `thorium`（默认） | 内置 Thorium（离线自包含） | 目标机无需装浏览器；B 站普通视频可直接播放 |
| `system` | 本机 Edge / Chrome | DRM 会员视频等需要复用本机登录态的场景，非离线 |

在 `config.json` 中修改 `browser_backend` 切换。

---

## 配置说明（config.json）

| 字段 | 说明 |
| --- | --- |
| `password_hash` | 访问密码的 Argon2 哈希（首次运行自动生成，不要手改） |
| `encryption_salt` | 随机盐，用于派生 Fernet 密钥（自动生成，不要手改） |
| `second_password_hash` | 二重导出密码的哈希（首次导出时自动生成，不要手改） |
| `log_level` | 日志级别：`DEBUG` / `INFO` / `WARNING` / `ERROR` / `CRITICAL` |
| `log_retention_days` | 日志保留天数 |
| `browser_backend` | `thorium`（默认）或 `system` |
| `browser.headless` | 是否无头模式（无头下 5 秒自动关闭） |
| `browser.window_size` | `maximized` 或 `1280x720` 等 |

> `password_hash` 与 `encryption_salt` 必须成对保留，改动任一个都会导致 `cookies.enc` 无法解密。

---

## 支持的 Cookie 格式

1. **分号字符串**：`SESSDATA=abc; bili_jct=def`
2. **标准 JSON 数组**：`[{"name":"SESSDATA","value":"abc","domain":".bilibili.com","path":"/"}]`
3. **Cookie-Editor 导出的 JSON**：自动映射 `expirationDate → expires`、`sameSite` 取值等

缺失的 `domain` 会用输入的域名补全（如 `.bilibili.com`），`path` 默认为 `/`。

---

## 安全与隐私

- 密码只以 Argon2 哈希存储，Cookie 用 Fernet 加密（密钥由密码 + 随机盐经 PBKDF2 200000 次迭代派生）
- 控制台与日志都不打印 Cookie 值（敏感字段自动脱敏，保留前 6 位）
- 每次运行使用独立临时目录，浏览器进程全部退出后连同 Cookie 一起删除
- 忘记密码 = Cookie 无法找回，只能删除 `cookies.enc` 重新添加

---

## 常见问题

**Q：提示「未找到内置 Thorium 浏览器」**
A：把 Thorium 解压内容放到 `external_browsers\thorium-win64\`（含 `thorium.exe` 与版本资源目录），或改用 `system` 后端。

**Q：提示「未检测到本机 Edge/Chrome」**
A：`system` 后端需要本机装有 Edge 或 Chrome；没有的话把 `browser_backend` 改回 `thorium`。

**Q：B 站视频无法播放 / 提示浏览器版本过低**
A：确认使用 `thorium` 后端（自带专有编解码）；`system` 后端播放能力取决于本机浏览器。

**Q：如何修改配置进入修改模式？**
A：不需要。密码验证后主菜单直接选择 2 即可。

**Q：Ctrl+C 中断会怎样？**
A：优雅退出并清理浏览器临时目录；采集过程按回车前中断则取消采集。

---

## 项目结构

```
CookieLuncher/
├── main.py                   # 程序入口
├── export_for_action.py      # 命令行导出脚本
├── config.json               # 配置（首次运行生成）
├── cookies.enc               # 加密 Cookie 存储（首次运行生成）
├── action_config_output.json # 导出产物（可提交 GitHub）
├── logs/                     # 按日期的日志（含脱敏）
├── external_browsers/        # Thorium（thorium 后端）
├── core/
│   ├── config.py             # 配置读写
│   ├── crypto.py             # 加密 / 解密 / 密码派生 / 哈希
│   ├── cookie_parser.py      # Cookie 格式解析
│   ├── cookie_capture.py     # 浏览器手动登录采集
│   ├── storage.py            # cookies.enc 读写
│   ├── exporter.py           # 导出 GitHub Action 配置（UI / CLI 共用）
│   ├── logger.py             # 日志与脱敏
│   ├── browser.py            # 双后端浏览器启动 / 注入 / 清理
│   └── ui.py                 # 控制台菜单交互
├── requirements.txt
└── README.md
```

---

## 技术要点（简版）


- **双后端参数一致**：thorium 与 system 共用同一套启动参数与注入逻辑，无分支差异。
- **采集不过滤**：手动登录采集到的有效 Cookie 全部保留，只剔除缺失关键字段或完全重复的条目。
- **导出只含密文**：`action_config_output.json` 不含明文 Cookie、不含盐、不含密码。
