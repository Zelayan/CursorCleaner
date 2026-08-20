# Cursor Cleaner

Cursor Cleaner 是 Windows 与 macOS（Apple Silicon）共用的 Avalonia 桌面工具，用于扫描并清理 Cursor 数据。默认工作流是扫描历史会话并按选择删除；工作区、按保留期批量清理和 SQLite `VACUUM` 需要在设置中打开对应开关后才会出现在界面上。

> [!IMPORTANT]
> 启动应用只会加载设置和显示界面，**默认不会启动扫描，也不会自动删除任何文件**。扫描必须由用户手动触发；扫描和分析均为只读操作。删除所选会话或执行批量清理前会显示确认框，确认后才备份并删除。

## 功能

- 扫描 Cursor、Cursor Insiders（仅在兼容目录已存在时）数据根，统计总占用和分类占用。
- 总览显示分类占用和全部扫描文件；历史会话页列出 `chats`、`sessions`、`agent-transcripts` 中的 JSON/JSONL，以及 `state.vscdb` / `conversation-search.db` 中的 Composer 聊天。同一 UUID 会合并成一行；`projects` 下的 MCP 工具定义、`package.json` 和 `node_modules` 不会当作会话。
- 历史会话页可只读预览 JSON/JSONL 内容。仅存在于 SQLite 的会话可以选中删除，但不解析数据库正文。
- 清理前可自动备份。删除可选择回收站 / 废纸篓，或在设置中显式改为永久删除。
- 设置中开启“显示高级功能”后，可使用工作区页和按 7/30/90 天或自定义日期生成的批量清理预览。
- 设置中开启“启用 SQLite 高级工具”后，可对已批准根内的数据库执行完整性检查、在线备份和 `VACUUM`。
- 支持系统、浅色和深色主题；设置和操作日志持久化到用户目录。

## 页面

| 页 | 何时显示 |
| --- | --- |
| 总览 | 始终 |
| 历史会话 | 始终 |
| 工作区 | 「显示高级功能」开启 |
| 空间分析 | 「显示高级功能」开启 |
| 高级工具 | 「启用 SQLite 高级工具」开启 |
| 设置 | 始终 |

## 工作流

默认工作流为：

```text
扫描 -> 选择历史会话 -> 用户确认 -> 备份文件和数据库 -> 删除 SQLite 记录 -> 回收会话文件
```

1. 用户点击“扫描”。扫描器只读取所有已识别的 Cursor 数据根，并生成内存中的文件快照。
2. 总览显示分类占用和文件列表；设置页的扫描范围开关只筛选显示结果和批准清理根，底层扫描器仍会读取全部 Cursor 数据根。
3. 用户在历史会话页选择要删除的会话。删除所选会话不受 7/30/90 天保留期限制。会话文件仍只接受 `ChatSession` 和 `AgentTranscript`；SQLite 聊天按会话 UUID 删除对应行，不把数据库文件送进回收站或废纸篓。
4. 确认框显示文件数、预计释放空间、备份模式、删除模式，以及将尝试删除 SQLite 中对应聊天。必须关闭 Cursor。该清理计划只能提交执行一次。
5. 对可匹配的会话 ID，先校验并在线备份 `state.vscdb` / `conversation-search.db`，再删除 `composerHeaders`、`cursorDiskKV` 白名单键和搜索索引中的对应记录。结构不认识或备份失败时该库不写，并明确提示。
6. 默认对会话文件创建一次备份会话；某文件备份失败时，该文件不会被删除。随后再次验证路径、大小、时间和扫描时捕获的文件身份，验证通过才执行所选删除方式。
7. 清理结束后重新扫描并显示文件删除数量、SQLite 删除行数、释放空间和当前占用。

按保留期清理工作区和会话可在设置中开启高级功能后，于空间分析页生成预览再执行。

设置允许关闭自动备份，关闭后普通清理会跳过文件备份；SQLite 维护不受该开关影响，始终先创建并校验在线备份，再执行 checkpoint 或 `VACUUM`。

## 安全边界

- Cursor 运行时仍允许只读扫描和分析，但 UI 与底层服务都会阻止实际清理和 SQLite 聊天删除；SQLite `VACUUM` 也会被阻止。关闭 Cursor 后再删除。
- 清理仅接受位于已批准 Cursor 数据根内的现有普通文件，不允许对数据根本身操作，也拒绝目标路径中的重解析点 / 符号链接。
- 执行前会比较文件大小、最后修改时间以及扫描时捕获的文件身份（Windows 为卷序列号 + 文件索引，macOS 为 `st_dev` + `st_ino`）；文件已消失、被替换或发生变化时跳过。
- 普通清理会拒绝 SQLite、“其他”以及未记录文件身份的项目。SQLite 主库以及 `-wal`、`-shm`、`-journal` sidecar 不会作为普通文件清理。
- 删除所选会话时，SQLite 聊天删除走单独路径：Cursor 已关闭 -> 路径守卫 -> 完整性检查 -> 在线备份并校验 -> 按会话 ID 删除白名单行。不执行 `DROP`、不整表清空、不 `VACUUM`。
- 默认使用 Windows 回收站或 macOS 废纸篓。**回收 / 废纸篓操作失败时只记录该文件失败，不会降级为永久删除。** 只有用户在设置中主动关闭该选项并在确认框确认后，普通清理才调用永久删除。
- macOS 废纸篓通过 Finder / Apple Events 完成。系统可能弹出自动化权限；拒绝后该项删除失败，文件会留在原处。
- 默认自动备份。备份目录空间不足、复制失败或清单写入失败时，相关删除不会继续。读不到可用空间时跳过该预检并继续尝试备份，不把“读不到”当成“空间足够”。
- 应用以当前用户权限运行，不请求管理员权限。

## 数据目录

### Windows

| 用途 | 路径 |
| --- | --- |
| Cursor Roaming 数据 | `%APPDATA%\Cursor` |
| Cursor Local 数据 | `%LOCALAPPDATA%\Cursor` |
| Cursor 用户数据 | `%USERPROFILE%\.cursor` |
| Cursor Insiders 兼容目录 | `%APPDATA%\Cursor - Insiders`、`%LOCALAPPDATA%\Cursor - Insiders`、`%USERPROFILE%\.cursor-insiders`（存在时加入） |
| 备份 | `%USERPROFILE%\CursorCleanerBackup\yyyy-MM-dd_HHmmss[_N]` |
| 日志 | `%LOCALAPPDATA%\CursorCleaner\logs\yyyy-MM-dd.log` |
| 设置 | `%LOCALAPPDATA%\CursorCleaner\settings.json` |

### macOS（Apple Silicon）

| 用途 | 路径 |
| --- | --- |
| Cursor Application Support 数据 | `~/Library/Application Support/Cursor` |
| Cursor Caches 数据 | 若存在 `~/Library/Caches/Cursor` 则使用，否则与 Application Support 去重 |
| Cursor 用户数据 | `~/.cursor` |
| Cursor Insiders 兼容目录 | `~/Library/Application Support/Cursor - Insiders`、`~/Library/Caches/Cursor - Insiders`、`~/.cursor-insiders`（存在时加入） |
| 备份 | `~/CursorCleanerBackup/yyyy-MM-dd_HHmmss[_N]` |
| 日志 | `~/Library/Application Support/CursorCleaner/logs/yyyy-MM-dd.log` |
| 设置 | `~/Library/Application Support/CursorCleaner/settings.json` |

日志为每行一个 JSON 对象。写日志失败不会掩盖主要操作结果。真实 Cursor 目录布局可能因版本而异；路径适配集中在 `CursorPathService`。

## 设置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| 保留天数 | `30` | 可选 `7`、`30`、`90`；只用于空间分析页的批量预览，不影响“删除所选会话” |
| 清理前自动备份 | 开启 | 仅控制普通清理；SQLite 维护始终备份 |
| 使用回收站 / 废纸篓 | 开启 | Windows 为“使用 Windows 回收站”，macOS 为“使用废纸篓” |
| 扫描范围 | 全部开启 | 只影响显示、分析和批准清理根 |
| 显示高级功能 | 关闭 | 开启后显示工作区和空间分析页 |
| SQLite 高级工具 | 关闭 | 开启后才可执行数据库维护 |
| 主题 | 跟随系统 | 也可选择浅色或深色 |

## 环境要求

- 开发和构建：.NET 8 SDK。仓库的 `global.json` 固定 `8.0.424`，并允许滚动到同一功能带的最新补丁。
- Windows 发布产物：`win-x64` 自包含单文件。目标机器不需要安装 .NET。
- macOS 发布产物：仅 `osx-arm64`（Apple Silicon），输出未签名的 `CursorCleaner.app`。不做 Intel Mac、公证、签名、DMG 或自动更新。
- .NET 8 于 **2026-11-10** 结束支持。

本仓库可在 Windows 上完成还原、编译、单元测试，以及交叉生成 `osx-arm64` 的 `.app` 目录。该 `.app` 无法在 Windows 上启动。macOS 废纸篓、Finder 自动化、`stat` 布局、真实 Cursor 目录和进程名需要 Apple Silicon Mac 实机验收。

## 构建、测试与运行

```powershell
dotnet restore .\CursorCleaner.sln --locked-mode
dotnet build .\CursorCleaner.sln -c Debug --no-restore
dotnet test .\CursorCleaner.Tests\CursorCleaner.Tests.csproj -c Debug --no-build
dotnet run --project .\CursorCleaner.Desktop\CursorCleaner.Desktop.csproj -c Debug
```

首次生成或更新锁文件时，去掉 `--locked-mode` 再执行 `dotnet restore`。

## 发布

### Windows x64

```powershell
dotnet restore .\CursorCleaner.Desktop\CursorCleaner.Desktop.csproj --locked-mode -r win-x64
dotnet publish .\CursorCleaner.Desktop\CursorCleaner.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore
```

默认输出：

```text
CursorCleaner.Desktop\bin\Release\net8.0\win-x64\publish\CursorCleaner.exe
```

单文件宿主会把原生 SQLite 提取到 `%TEMP%\.net\...`。当前用户必须能写入该目录。

### macOS Apple Silicon

```bash
dotnet restore ./CursorCleaner.Desktop/CursorCleaner.Desktop.csproj --locked-mode -r osx-arm64
dotnet publish ./CursorCleaner.Desktop/CursorCleaner.Desktop.csproj -c Release -r osx-arm64 --self-contained true --no-restore
```

默认输出：

```text
CursorCleaner.Desktop/bin/Release/net8.0/osx-arm64/publish/CursorCleaner.app
```

结构为：

```text
CursorCleaner.app/Contents/Info.plist
CursorCleaner.app/Contents/MacOS/   # 可执行文件 CursorCleaner 与依赖
```

Bundle 标识为 `dev.cursorcleaner.app`。第一版不做 codesign / 公证；未签名应用在其他 Mac 上可能被 Gatekeeper 拦截。不要把回收 / 废纸篓失败改成永久删除来规避权限问题。

## 实机验收（Apple Silicon）

- 启动 `.app`，主题“跟随系统”能随浅色/深色切换。
- 扫描真实 Cursor 根：`~/Library/Application Support/Cursor`、`~/.cursor`，以及存在时的 Caches / Insiders。
- 设置和日志写在 `~/Library/Application Support/CursorCleaner`。
- Cursor 运行时（含 Helper 进程）禁止清理；退出后再删一条会话，文件进入废纸篓。
- 若拒绝 Finder 自动化权限，该项失败且原文件仍在。
- 文件身份变更（替换同名文件）不得按旧快照删除。

## 测试说明

自动化测试使用临时合成目录，覆盖扫描分类、去重、分析、预览、路径守卫、文件身份、回收失败不降级、设置恢复和 SQLite 维护。真实回收站、废纸篓、主题切换和窗口交互仍需人工验收。测试不会对真实 Cursor 目录执行清理。

## 项目结构

```text
cursorclear\
|-- CursorCleaner.sln
|-- global.json
|-- CursorCleaner.Core\             # net8.0 业务层
|-- CursorCleaner.Desktop\          # Avalonia 11 界面
|   `-- Mac\Info.plist
`-- CursorCleaner.Tests\            # MSTest，只引用 Core
```
