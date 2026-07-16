# Pixora 接手维护说明

这份文件给后续接手项目的人和 AI 编程助手使用。修改前请先读完本文件、`README.md`、`docs/ARCHITECTURE.md` 和 `docs/RELEASE.md`，再开始动代码。

## 基本约定

- 用户可见的软件名是 `Pixora`。
- 源码项目、命名空间、解决方案和发布包统一使用 `Pixora`，例如 `Pixora.sln`、`src\Pixora`、`namespace Pixora`。
- `Pic-O` 和 `PureView` 只作为旧版本兼容名称保留，用于本地数据迁移、历史 Git 记录和必要的发布说明。
- 默认用简体中文写面向用户的说明、错误提示、发布说明和维护文档。
- 代码、类型名、文件名、注册表项、命令和配置键保持原文。
- 项目可以说明“由 AI 辅助编程完成”，但不要写具体 AI 工具、模型、账号、本机用户名、绝对工作目录、测试素材私有路径或访问令牌。
- 不要提交构建产物和本地测试产物：`bin/`、`obj/`、`publish/`、`test-output/`、`.artifacts*/`。
- `tools\SetUserFTA.exe` 是用户自行放置的可选外部工具，不能提交到仓库，也不能默认内置到发布包。

## 快速命令

```powershell
dotnet build Pixora.sln
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
.\publish.ps1 -Zip
```

带外部样本目录的压力测试示例：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj -- --media-folder "D:\Samples" --sample-count 50 --video-sample-count 5
```

检查仓库状态：

```powershell
git status --short --branch
```

## 接手时先看哪里

- `README.md`：面向普通用户和开源仓库首页。
- `docs\ARCHITECTURE.md`：工程结构、核心流程、服务职责、设置文件和技术债。
- `docs\RELEASE.md`：版本号、构建、测试、发布包检查、GitHub Release 和隐私扫描。
- `src\Pixora\AppInfo.cs`：品牌名、数据目录、文件关联 ProgId、图标路径、旧数据迁移。
- `src\Pixora\MainWindow.xaml` 和 `src\Pixora\MainWindow.xaml.cs`：主窗口、打开图片、目录补齐、缩略图栏、快捷键入口、收藏、裁剪、压缩、壁纸、批量删除等大部分用户工作流。
- `src\Pixora\Services`：图片加载、视频封面、缓存、目录索引、设置、快捷键、文件关联、收藏、压缩和错误日志。
- `tests\Pixora.SmokeTests\Program.cs`：当前自动测试覆盖面，也是改动后最先跑的回归入口。

## 重要数据和配置

应用数据目录：

```text
%LOCALAPPDATA%\Pixora
```

当前会写入或读取的主要文件：

- `viewer-settings.json`：主设置、缩略图栏、排序、缓存、设置窗口位置、上次目录等。
- `shortcuts.json`：快捷键配置。
- `favorites.json`：收藏路径。
- `batch-compression-settings.json`：批量压缩窗口上次输入和参数。
- `error.log`：异常日志。
- `thumbnail-cache\`：可选缩略图磁盘缓存。

旧版本数据目录是：

```text
%LOCALAPPDATA%\Pic-O
%LOCALAPPDATA%\PureView
```

启动时 `App.OnStartup` 会调用 `AppInfo.EnsureLocalDataMigrated()`，只在目标文件不存在时把旧目录里的设置、快捷键、收藏和批量压缩设置复制到新目录。这个迁移是 best-effort，失败不能阻止启动。

## 修改功能时的检查点

图片或视频格式相关改动：

- 同步检查 `ImageCatalog` 的扩展名列表。
- 同步检查 `FileAssociationService` 的扩展名列表。
- 如果是图片解码能力，检查 `ImageLoader` 的 WIC / Magick.NET 路径。
- 如果是视频封面，检查 `VideoThumbnailLoader` 和 Shell 缩略图 fallback。
- 更新 `README.md` 的限制说明和 smoke test。

设置项相关改动：

- 在 `ViewerSettings` 或对应 settings 类型里加属性和默认值。
- 在设置窗口里加载、保存、恢复默认值。
- 加入 smoke test 覆盖持久化。
- 考虑旧 JSON 没有该字段时的默认行为。

快捷键相关改动：

- 修改 `Models\ShortcutAction.cs`。
- 修改 `ShortcutSettings.ActionInfos`、默认快捷键和排序权重。
- 修改主窗口执行分发逻辑。
- 修改 `ShortcutSettingsWindow` 的显示、搜索和冲突提示。
- 更新 smoke test。

文件关联和默认应用相关改动：

- 优先保持“注册到打开方式列表”和“设置为默认应用”两个概念分离。
- 普通注册写 `HKCU\Software\Classes`、`HKCU\Software\RegisteredApplications` 和 `Capabilities`。
- 现代 Windows 通常禁止普通应用静默改默认应用。官方路径应打开系统默认应用设置页，让用户确认。
- 高级一键默认依赖外部 `SetUserFTA.exe`，只在用户明确放置工具并确认后执行。
- 不要把绕过 Windows 默认应用保护的工具、脚本或二进制文件放进仓库。

发布包相关改动：

- Release 配置不输出 PDB，避免本机路径进入发布包。
- 发布包必须包含 `LICENSE`。
- 发布包不应包含 `Pixora.pdb`。
- `tools\SetUserFTA.exe` 只有用户本地自行放置时才会被条件复制，仓库和正式开源包不应包含它。

## 测试策略

每次代码改动至少运行：

```powershell
dotnet build Pixora.sln
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

涉及图片解码、缩略图、大目录性能或视频封面时，再用真实素材目录跑外部样本参数：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj -- --media-folder "D:\Samples" --sample-count 50 --video-sample-count 5
```

涉及 UI 体验时，至少手动检查：

- 直接打开一张图片。
- 打开包含几百张和更多文件的目录。
- 从文件资源管理器双击图片启动。
- 缩略图栏单列/双列。
- 收藏视图切换。
- 设置窗口大小和位置持久化。
- 快捷键搜索、修改、冲突提示和恢复默认。
- 图片压缩、批量压缩、裁剪和圆形裁剪。
- GIF/APNG 这类动图不会显示“设为桌面壁纸”。

## Git 和 GitHub

- 默认在 `main` 分支工作。
- 提交前运行 `git status --short --branch`，确认只提交本次相关文件。
- 文档改动可以单独提交；代码改动要附带测试或说明为什么不需要。
- 版本发布建议使用 `vX.Y.Z` 标签，例如 `v0.3.0`。
- GitHub Release 附件优先上传 `publish\Pixora-win-x64.zip`。
- 已公开发布的 Release 默认保持不可变；较多功能或代码改动必须升级补丁版本并创建新 Release，例如从 `0.3.7` 升级到 `0.3.8`，不要覆盖旧版本附件。
- 只有同版本构建损坏、附件缺失等明确的发布事故，并且用户明确要求覆盖时，才可以替换现有 Release 附件；替换后要在 Release 正文记录日期和原因。

## 隐私和仓库清理

提交前至少扫描一次。下面命令使用占位符，执行前把关键词替换成你本机需要排查的内容；不要把真实用户名、私有路径、令牌或具体 AI 工具/模型名提交进仓库。

```powershell
rg -n -i "<具体AI工具或模型名>|<具体AI平台名>" . -g "!**/bin/**" -g "!**/obj/**" -g "!publish/**" -g "!test-output/**" -g "!.git/**"
rg -n -i "<本机用户名>|<私有绝对路径>|<私有样本目录>|<访问令牌片段>" . -g "!**/bin/**" -g "!**/obj/**" -g "!publish/**" -g "!test-output/**" -g "!.git/**"
```

允许保留泛化描述，例如 README 中的“本项目由 AI 辅助编程完成”。如果扫描命中具体工具名、私有路径、用户名或令牌，先确认来源，再删除或改成泛化表述。
