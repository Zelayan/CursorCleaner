# Cursor Cleaner

Cursor Cleaner 是面向 Windows 的 WPF 工具，用于扫描、分析并按明确计划清理 Cursor 的历史会话、Workspace 数据和 Agent Transcript，也提供受保护的 SQLite 数据库检查、备份与 `VACUUM` 功能。

> [!IMPORTANT]
> 启动应用只会加载设置和显示界面，**默认不会启动扫描，也不会自动删除任何文件**。扫描必须由用户手动触发；扫描和分析均为只读操作。实际清理必须先生成 Dry Run 预览，再经用户确认。

## 功能

- 扫描 Cursor、Cursor Insiders（仅在兼容目录已存在时）数据根，统计文件数、占用、分类和 Top 50 大文件。
- 分类显示历史会话、Workspace、SQLite、Agent Transcript 和其他文件。历史会话只包含 `chats`、`sessions` 和 `agent-transcripts` 中的 JSON/JSONL；`projects` 下的 MCP 工具定义、`package.json` 和 `node_modules` 不会当作会话。
- 从有限大小的 JSON/JSONL 内容中提取会话标题和项目名；分析 `workspace.json` 中的本地 `file:` URI，并标记已经不存在的项目路径。
- 历史会话页可只读预览选中的 JSON/JSONL 会话内容（含 Cursor agent transcript 的 `role`/`message.content[].text` 与 `<user_query>`）。预览不会写入 Cursor 数据或 SQLite；保存在数据库中的聊天当前不解析。
- 按 7、30、90 天或自定义截止日期生成清理预览，也可从选中的会话或 Workspace 生成预览。
- 清理前可自动备份，删除可选择 Windows 回收站或显式永久删除。
- 高级工具可对 `.vscdb`、`.db`、`.sqlite` 主数据库先做只读 `PRAGMA quick_check` 和在线备份校验，通过后再执行 WAL checkpoint 和 `VACUUM`。
- 支持系统、浅色和深色主题；设置和操作日志持久化到用户目录。

## 工作流与 Dry Run

真实工作流为：

```text
扫描 -> 分析 -> Dry Run（生成清理预览） -> 用户确认 -> 备份 -> 删除
```

1. 用户点击“扫描”。扫描器只读取所有已识别的 Cursor 数据根，并生成内存中的文件快照。
2. 应用分析 Workspace、会话、空间占用和 SQLite 文件；设置页的扫描范围开关只筛选显示结果和批准清理根，底层扫描器仍会读取全部 Cursor 数据根。
3. 用户选择保留期限、截止日期或具体项目，生成 Dry Run 清理预览。预览只包含截止时间之前的 Workspace、历史会话和 Agent Transcript 文件，不包含 SQLite 或“其他”文件。
4. 用户点击“开始清理”，应用显示文件数、预计释放空间、备份模式和删除模式，并要求明确确认。预览计划只能提交执行一次。
5. 默认对整个清理计划创建一次备份会话；某文件备份失败时，该文件不会被删除。随后再次验证路径、大小、时间和扫描时捕获的文件身份，验证通过才执行所选删除方式。
6. 清理结束后重新扫描并显示实际删除数量、释放空间和当前占用。取消清理后也会用独立令牌重新扫描，避免界面继续显示已删除文件。

设置允许关闭自动备份，关闭后普通清理会跳过第 5 步的备份；SQLite 维护不受该开关影响，始终先创建并校验在线备份，再执行 checkpoint 或 `VACUUM`。未通过校验的备份路径不会作为有效备份显示。

## 安全边界

- Cursor 运行时仍允许只读扫描和分析，但 UI 与底层服务都会阻止实际清理；SQLite 维护也会被阻止。关闭 Cursor 后应重新生成清理预览。
- 清理仅接受位于已批准 Cursor 数据根内的现有普通文件，不允许对数据根本身操作，也拒绝目标路径中的重解析点。
- 执行前会比较文件大小、最后修改时间以及扫描时捕获的文件身份；文件已消失、被替换或发生变化时跳过，不按过期快照删除。
- 普通清理的预览生成器和执行服务都会拒绝 SQLite、“其他”以及未记录文件身份的项目。SQLite 主库以及 `-wal`、`-shm`、`-journal` sidecar 不会作为普通文件清理。
- SQLite 高级工具默认关闭，只接受已批准根内的 `.vscdb`、`.db`、`.sqlite` 文件。顺序为：Cursor 已关闭 -> 只读完整性检查 -> 在线备份并校验 -> checkpoint -> `VACUUM`。完整性检查或备份失败时不执行写操作，也不会把未校验路径显示为备份。
- 默认使用 Windows 回收站。**回收站操作失败时只记录该文件失败，不会降级为永久删除。** 只有用户在设置中主动关闭“使用 Windows 回收站”并在确认框确认后，普通清理才调用永久删除。
- 默认自动备份。备份目录空间不足、复制失败或清单写入失败时，相关删除不会继续。
- 应用以当前用户权限运行，不请求管理员权限，也不能消除文件系统权限、磁盘故障、外部并发修改或 Cursor 数据格式变化带来的风险。

## 数据目录

以下路径均为 Windows 环境变量形式，路径中的代码均为 ASCII：

| 用途 | 路径 |
| --- | --- |
| Cursor Roaming 数据 | `%APPDATA%\Cursor` |
| Cursor Local 数据 | `%LOCALAPPDATA%\Cursor` |
| Cursor 用户数据 | `%USERPROFILE%\.cursor` |
| Cursor Insiders 兼容目录 | `%APPDATA%\Cursor - Insiders`、`%LOCALAPPDATA%\Cursor - Insiders`、`%USERPROFILE%\.cursor-insiders`（存在时加入） |
| 备份 | `%USERPROFILE%\CursorCleanerBackup\yyyy-MM-dd_HHmmss[_N]` |
| 备份清单 | 每次备份目录中的 `manifest.json` |
| 日志 | `%LOCALAPPDATA%\CursorCleaner\logs\yyyy-MM-dd.log` |
| 设置 | `%LOCALAPPDATA%\CursorCleaner\settings.json` |

日志为每行一个 JSON 对象，记录 UTC 时间、级别、操作、消息、相关路径和异常摘要。日志不是事务审计系统，写日志失败不会掩盖主要操作结果。

## 设置

首次运行或设置文件损坏时使用以下默认值：

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| 保留天数 | `30` | 可选 `7`、`30`、`90`；只计划删除修改时间严格早于截止时间的文件 |
| 清理前自动备份 | 开启 | 仅控制普通清理；SQLite 维护始终备份 |
| 使用 Windows 回收站 | 开启 | 关闭后普通清理使用永久删除，确认框会明确显示模式 |
| Roaming、Local、用户目录范围 | 全部开启 | 只影响显示、分析和批准清理根，不缩小底层扫描读取范围 |
| SQLite 高级工具 | 关闭 | 开启后才可执行数据库维护 |
| 主题 | 跟随系统 | 也可选择浅色或深色 |

设置更改会使现有清理预览失效；应保存设置并重新生成预览。损坏或不可读的 `settings.json` 会回退到默认值并尝试记录日志。

## 环境要求

- 开发和构建：Windows x64，以及 .NET 8 SDK。仓库的 `global.json` 固定 `8.0.424`，并允许滚动到同一功能带的最新补丁。
- 运行发布产物：Windows x64。发布配置为自包含单文件，**目标机器不需要安装 .NET**。
- .NET 8 于 **2026-11-10** 结束支持。届时应升级目标框架、SDK、`Microsoft.Data.Sqlite` 及发布流水线，并重新执行全部测试后再发布。

## 构建、测试与运行

在仓库根目录执行：

```powershell
dotnet restore .\CursorCleaner.sln --locked-mode
dotnet build .\CursorCleaner.sln -c Debug --no-restore
dotnet test .\CursorCleaner.Tests\CursorCleaner.Tests.csproj -c Debug --no-build
dotnet run --project .\CursorCleaner\CursorCleaner.csproj -c Debug
```

常规要求是把 .NET 8 SDK 的 `dotnet` 加入 `PATH`。当前开发机另有便携 SDK `C:\Users\Administrator\.dotnet8\dotnet.exe`，需要复现本机环境时可将上述命令中的 `dotnet` 替换为该完整路径；这不是项目对其他开发机的固定要求。

## 发布

项目文件已经配置 `win-x64`、自包含、单文件、ReadyToRun，并启用原生库自提取：

```powershell
dotnet restore .\CursorCleaner\CursorCleaner.csproj --locked-mode -r win-x64
dotnet publish .\CursorCleaner\CursorCleaner.csproj -c Release -r win-x64 --self-contained true --no-restore
```

默认输出位于：

```text
CursorCleaner\bin\Release\net8.0-windows\win-x64\publish\CursorCleaner.exe
```

发布的 `CursorCleaner.exe` 包含托管运行时和应用依赖，因此目标机无需安装 .NET。`Microsoft.Data.Sqlite` 使用的原生 SQLite 运行时不能直接从单文件包内加载，.NET bundle host 会在运行时将原生文件提取到 `%TEMP%\.net\...`。这意味着：

- 当前用户必须能在 `%TEMP%` 下创建、写入并加载提取文件；应用不是真正的“零落盘”可执行文件。
- 安全策略、应用控制、杀毒软件或临时目录清理若禁止或移除该目录，SQLite 功能乃至应用启动可能失败。
- `%TEMP%\.net` 中可能保留按应用和 bundle 标识创建的提取目录；应仅在相关进程退出后按组织策略清理，不能假设 EXE 退出时一定自动删除。

## 测试说明

自动化测试使用 `%TEMP%\CursorCleaner.Tests\<guid>` 下即时创建的合成目录、文件、JSON/JSONL 和 SQLite 数据库，覆盖扫描分类、重复根去重、重解析点跳过、分析器、会话内容只读预览、保留期限、路径保护、文件身份、禁止类别、整计划备份、快照变化、备份清单、Cursor 运行拦截、计划重复执行、回收站部分失败、设置恢复以及 SQLite 检查/备份/`VACUUM`。真实 Windows 回收站、高对比度运行时切换和完整窗口交互仍需人工验收。

自动化测试和功能验证仍只使用合成夹具，不会对真实 Cursor 目录执行备份、VACUUM 或清理。本机若存在 Cursor JSON/JSONL transcript，可在历史会话页做只读预览验收；测试结果不能替代在受控备份和非生产 Cursor 配置上的兼容性验收，尤其不能保证未来 Cursor 版本的数据布局或字段格式保持不变。

## 项目结构

```text
cursorclear\
|-- CursorCleaner.sln
|-- global.json
|-- CursorCleaner\
|   |-- CursorCleaner.csproj
|   |-- App.xaml(.cs)               # 启动、服务组装、主题和全局异常处理
|   |-- MainWindow.xaml(.cs)        # WPF 主界面和确认/错误对话框
|   |-- Models\                     # 扫描、分析、清理与 SQLite 数据契约
|   |-- Services\                   # 路径、扫描、分析、备份、清理、日志和 SQLite 实现
|   |-- ViewModels\MainViewModel.cs # 页面状态与完整用户工作流
|   |-- Helpers\                    # 命令、通知、路径安全和大小格式化
|   |-- Converters\                 # WPF 值转换器
|   `-- Resources\                  # 浅色和深色主题资源
`-- CursorCleaner.Tests\            # MSTest 合成夹具与服务测试
```
