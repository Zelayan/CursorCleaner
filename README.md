# Cursor Cleaner

Cursor Cleaner 是 Windows 与 macOS（Apple Silicon）共用的 Avalonia 桌面工具，用于扫描并清理 Cursor 数据。默认工作流是启动后自动分析占用，再按 7/30/90 天一键清理旧会话。工作区、空间分析和数据库页始终可用，无需额外开关。

> [!IMPORTANT]
> 启动后会自动执行**只读扫描**，不会删除任何文件。首页“清理 N 天前的数据”仍会显示一次确认框；取消确认不会执行任何写入。

## 功能

- 启动后自动扫描 Cursor、Cursor Insiders（仅在兼容目录已存在时）数据根，统计总占用和分类占用。
- 总览首页显示当前占用、7/30/90 天旧会话摘要，以及“清理 N 天前的数据”。默认只清理旧 `ChatSession` / `AgentTranscript`，不纳入工作区、SQLite 文件和其他缓存。
- 历史会话页列出 `chats`、`sessions`、`agent-transcripts` 中的 JSON/JSONL，以及 `state.vscdb` / `conversation-search.db` 中的 Composer 聊天。同一 UUID 会合并成一行；`projects` 下的 MCP 工具定义、`package.json` 和 `node_modules` 不会当作会话。
- 历史会话页可只读预览 JSON/JSONL 内容。仅存在于 SQLite 的会话可以选中删除，但不解析数据库正文；此类会话没有独立文件，大小列显示为「—」（不是 0 字节占用）。删除 SQLite 行后库文件通常不会立刻变小；首页会提供可选的“优化数据库”，不自动 `VACUUM`。
- 清理前可自动备份。删除可选择回收站 / 废纸篓，或在设置中显式改为永久删除。文件进入废纸篓后，清空废纸篓才会释放磁盘空间；备份仍占用空间。
- 工作区页可查看工作区明细，并为所选或全部数据生成清理预览。
- 空间分析页可按保留期或自定义日期生成预览并执行清理。
- 数据库页可选择具体 SQLite 库、只读分析占用、优化（VACUUM）、查看备份占用、打开备份目录、删除旧时间戳备份。
- 支持系统、浅色和深色主题；设置和操作日志持久化到用户目录。

## 页面

| 页 | 内容 |
| --- | --- |
| 总览 | 占用摘要与按时间一键清理 |
| 历史会话 | 会话列表、只读预览、多选删除 |
| 工作区 | 工作区明细与所选工作区预览 |
| 空间分析 | 自定义日期 / 保留期预览与执行清理 |
| 数据库 | 选择具体库、只读分析占用、优化、备份维护 |
| 设置 | 备份、备份目录、废纸篓、扫描范围、主题 |

## 工作流

默认工作流为：

```text
启动 -> 自动扫描 -> 显示占用 -> 选择 7/30/90 天 -> 清理 -> 一次安全确认 -> 自动重新扫描
```

1. 启动后自动扫描。扫描器只读取所有已识别的 Cursor 数据根，并生成内存中的文件快照；扫描不会修改任何文件。
2. 总览显示当前占用、按保留期计算的旧会话数量/文件字节，以及主按钮“清理 N 天前的数据”。设置页的扫描范围开关只筛选显示结果和批准清理根，底层扫描器仍会读取全部 Cursor 数据根。
3. 默认一键清理只处理修改时间早于截止日期的历史会话和代理转录。工作区、SQLite 数据库文件、sidecar 和其他缓存不纳入。有合法 UUID 的旧会话会同时清理匹配的 SQLite 白名单记录。
4. 确认框显示会话/文件数量、可从 Cursor 数据中移除的大小、SQLite 会话 ID 数量、废纸篓/备份说明。必须关闭 Cursor。取消确认不会写入。
5. 对可匹配的会话 ID，先校验并在线备份 `state.vscdb` / `conversation-search.db`，再删除 `composerHeaders`、`cursorDiskKV` 白名单键和搜索索引中的对应记录。结构不认识或备份失败时该库不写，并明确提示。
6. 默认对会话文件创建一次备份会话；备份整体失败（包括清单写入失败）时，本次计划不删除任何文件。随后再次验证路径、大小、时间和扫描时捕获的文件身份，验证通过才执行所选删除方式。
7. 清理结束后重新扫描并更新占用与可清理摘要。文案区分“已从 Cursor 数据目录移除”和“清空废纸篓后释放磁盘空间”。若本次删过 SQLite 行，首页提供可选的“优化数据库”，不自动 `VACUUM`。

历史会话页仍可手动多选删除，不受 7/30/90 天限制。按自定义日期清理工作区可在空间分析页生成预览再执行。

设置允许关闭自动备份，关闭后普通清理会跳过文件备份；SQLite 维护不受该开关影响，始终先创建并校验在线备份，再执行 checkpoint 或 `VACUUM`。SQLite 在线备份按数据库滚动保留最新一份，不会无限堆积；操作瞬间仍可能短暂占用约 2–3 倍库体积。

### 大数据库的空间预检

SQLite 维护开始前会按源库卷和备份卷做联合空间计划，而不是分别独立检查：

- 备份大小按 `主库 + WAL + SHM` 估算；`VACUUM` 工作空间按 2 倍主库预留；另加 `max(1 GiB, 10%)` 安全余量。
- 源库与备份目录在同一卷时，一次性计算整个流程的峰值（含已有滚动备份在提交后释放的抵扣），避免“两次各查一倍、实际同一份空间”导致的 `SQLITE_FULL`。
- 在线备份提交后、checkpoint 和 VACUUM 前会再次实测空间；期间被其他程序占满时保留已校验备份并跳过 VACUUM，原库不变。
- 空间不足时确认框直接显示各卷可用/需要/还差多少，不会让用户等待十几分钟后才失败。
- 会话清理只在目标库确有匹配记录后检查备份卷空间，不足时该库不写。

设置页可选择备份目录到其他磁盘（保存后下次启动生效）。选择外置盘可显著降低同卷峰值需求；旧备份目录不会被移动或删除。崩溃残留的过期 staging 文件会在下次维护前自动清理，但只限白名单文件名，`current.vscdb` 永不自动删除。

## 安全边界

- Cursor 运行时仍允许只读扫描和分析，但 UI 与底层服务都会阻止实际清理和 SQLite 聊天删除；SQLite `VACUUM` 也会被阻止。关闭 Cursor 后再删除。顶部提供「强制停止 Cursor」：确认后先尝试优雅退出，超时再结束 Cursor / Insiders 及 Helper 进程；未保存工作可能丢失。
- 数据库页的“分析占用”只读打开选中库（无需关闭 Cursor），按表、`cursorDiskKV` 键前缀和 ItemTable 最大键展示空间分布，并估算聊天记录可清理字节与空闲页。分析不会写入、不会删除任何键。
- 清理仅接受位于已批准 Cursor 数据根内的现有普通文件，不允许对数据根本身操作，也拒绝目标路径中的重解析点 / 符号链接。
- 执行前会比较文件大小、最后修改时间以及扫描时捕获的文件身份（Windows 为卷序列号 + 文件索引，macOS 为 `st_dev` + `st_ino`）；文件已消失、被替换或发生变化时跳过。
- 普通清理会拒绝 SQLite、“其他”以及未记录文件身份的项目。SQLite 主库以及 `-wal`、`-shm`、`-journal` sidecar 不会作为普通文件清理。
- 删除所选会话或按时间清理旧会话时，SQLite 聊天删除走单独路径：Cursor 已关闭 -> 路径守卫 -> 完整性检查 -> 仅在目标库确有匹配会话 ID 时在线备份并校验 -> 按会话 ID 删除白名单行。不执行 `DROP`、不整表清空、不在删除时 `VACUUM`。同一数据库只保留最新一份已校验备份；备份失败、空间不足或读不到空闲空间时不写原库。多库处理中取消或任一侧 SQLite 未完整成功时，**不会继续删除会话文件**，避免记录与文件不一致。界面会显示当前阶段（检查 / 备份百分比 / 删行）；大库备份可能需要很长时间。若需回收磁盘，删除并确认 Cursor 正常后可在首页或数据库页手动优化数据库（开始后不可取消；备份卷与源库卷都需约等量空闲；完成后会再 `quick_check`）。优化过程同样显示检查、备份百分比和 VACUUM 阶段。优化后若占用仍高，可能是滚动备份 `current.vscdb` 仍占空间。
- 默认使用 Windows 回收站或 macOS 废纸篓。**回收 / 废纸篓操作失败时只记录该文件失败，不会降级为永久删除。** 只有用户在设置中主动关闭该选项并在确认框确认后，普通清理才调用永久删除。
- macOS 废纸篓通过 Finder / Apple Events 完成。系统可能弹出自动化权限；拒绝后该项删除失败，文件会留在原处。
- 默认自动备份。备份目录空间不足、复制失败或清单写入失败时，相关删除不会继续。普通文件备份在读不到可用空间时仍会尝试；**SQLite 在线备份与 `VACUUM` 在读不到空闲空间时直接失败**，不把“读不到”当成“空间足够”。
- 应用以当前用户权限运行，不请求管理员权限。

## 数据目录

### Windows

| 用途 | 路径 |
| --- | --- |
| Cursor Roaming 数据 | `%APPDATA%\Cursor` |
| Cursor Local 数据 | `%LOCALAPPDATA%\Cursor` |
| Cursor 用户数据 | `%USERPROFILE%\.cursor` |
| Cursor Insiders 兼容目录 | `%APPDATA%\Cursor - Insiders`、`%LOCALAPPDATA%\Cursor - Insiders`、`%USERPROFILE%\.cursor-insiders`（存在时加入） |
| 文件备份 | `%USERPROFILE%\CursorCleanerBackup\yyyy-MM-dd_HHmmss[_N]` |
| SQLite 滚动备份 | `%USERPROFILE%\CursorCleanerBackup\sqlite\<库名>_<hash>\current.vscdb` |
| 日志 | `%LOCALAPPDATA%\CursorCleaner\logs\yyyy-MM-dd.log` |
| 设置 | `%LOCALAPPDATA%\CursorCleaner\settings.json` |

### macOS（Apple Silicon）

| 用途 | 路径 |
| --- | --- |
| Cursor Application Support 数据 | `~/Library/Application Support/Cursor` |
| Cursor Caches 数据 | 若存在 `~/Library/Caches/Cursor` 则使用，否则与 Application Support 去重 |
| Cursor 用户数据 | `~/.cursor` |
| Cursor Insiders 兼容目录 | `~/Library/Application Support/Cursor - Insiders`、`~/Library/Caches/Cursor - Insiders`、`~/.cursor-insiders`（存在时加入） |
| 文件备份 | `~/CursorCleanerBackup/yyyy-MM-dd_HHmmss[_N]` |
| SQLite 滚动备份 | `~/CursorCleanerBackup/sqlite/<库名>_<hash>/current.vscdb` |
| 日志 | `~/Library/Application Support/CursorCleaner/logs/yyyy-MM-dd.log` |
| 设置 | `~/Library/Application Support/CursorCleaner/settings.json` |

日志为每行一个 JSON 对象。写日志失败不会掩盖主要操作结果。真实 Cursor 目录布局可能因版本而异；路径适配集中在 `CursorPathService`。

## 设置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| 保留天数 | `30` | 可选 `7`、`30`、`90`；用于首页一键清理和空间分析预览，不影响“删除所选会话” |
| 清理前自动备份 | 开启 | 仅控制普通清理；SQLite 维护始终备份，且同一库只保留最新一份 |
| 备份目录 | `~/CursorCleanerBackup` | 可选择其他磁盘；保存后下次启动生效，旧目录不迁移 |
| 使用回收站 / 废纸篓 | 开启 | Windows 为“使用 Windows 回收站”，macOS 为“使用废纸篓” |
| 扫描范围 | 全部开启 | 只影响显示、分析和批准清理根 |
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
