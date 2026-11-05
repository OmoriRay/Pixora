# Pixora Agent 交接报告

> 2026-11-06 0.3.12 发布交接：本轮交付版本为 `0.3.12`，源码同步到 `main`，使用独立新标签 `v0.3.12` 和对应下载包，保留此前 Release。发布说明见 `docs/RELEASE-v0.3.12.md`，测试证据见 `docs/VALIDATION-2026-11-05.md`。本机常用安装位置以有效的文件关联入口为准，不在公开文档记录私有路径。下面“未提交、未发布、未替换”等说明属于当时验收阶段的历史记录；实时版本状态以 Git 和 GitHub Release 为准。

> 2026-11-06 验收完成：真实手机拔线恢复后，第 6 项重试以及第 7、8 项首次读取通过，缓存尺寸及可解码性另行核对通过。今日重新构建及完整 smoke tests（4/4 手机样本）通过；本地 500 项目录、缩略图单/双列、收藏、设置位置尺寸、快捷键保存执行/冲突/恢复默认、矩形/圆形裁剪、单图/批量压缩、GIF 壁纸菜单与播放暂停、Release 测试包启动均已通过界面检查，导出文件另行核验。测试程序全部关闭，4 项配置及对应备份共 8 个文件已恢复，并逐一核对 SHA-256。加载重叠及连续双击竞速使用自动门控/管道回归证据，未当作实机并发通过；500 项使用小图，不代表真实大图目录性能。最新详情见 `docs/VALIDATION-2026-11-05.md`。本轮未替换旧安装目录，未提交、推送或公开发布。

> 2026-11-05 验收进行中：用户已改为要求执行全部测试。当前 Debug 构建及 Release 测试发布构建成功，完整 smoke tests（含 11 项管道确认及 4/4 真实手机截图）通过；真实设备已验证首图、长截图切换、本地目录切换和原始副本保存。实机进一步发现缓存缺失时的 MTP 目录重新解析问题，已修改重连逻辑并重新通过自动回归，正在继续实机复测。最新状态见 `docs/VALIDATION-2026-11-05.md`，下面“未测试”说明仅保留为历史记录。尚未更新旧安装目录、提交、推送或公开发布。

> 2026-10-24 后续开发（待用户验证）：新增并行单实例请求接收与旧请求抑制、手机重连重试、原始副本保存，以及对应管道/主窗口/传输/导出行为回归。本轮按用户要求未运行构建、自动测试或手动测试，也未更新本地安装包。下方此前的“验收通过”和程序集哈希仅适用于上一轮构建，不能用于当前新增代码。测试流程见 `docs/MOBILE-RECOVERY-TESTING.md`。

> 2026-10-24 验收补充：已修复手机临时文件来源识别、打开会话串目录，以及单实例转发进程过早退出导致临时文件被系统清理的问题。完整构建为 0 警告、0 错误，smoke tests 通过（含 7 项真实命名管道确认回归）。设备目录 8 张真实图片均通过读取验证，最大样本为 1440 × 12123；已验证运行中再次从资源管理器打开手机图片，以及键盘上一张/下一张仍停留在设备目录。发布目录和本地安装目录已同步，核心程序集 SHA256 均为 `952A3E2B13709A286D44062AC2BFBB0E90137AB9D7BD3A426FE8DC8A33BC1A20`。尚未提交、推送或公开发布。使用时仍需保持手机可访问、资源管理器手机源目录打开；来源无法唯一识别时仅打开单图。

> 2026-10-24 工作区补充：以下旧快照保留用于历史交接；当前未提交改动还包含 EXIF 方向、缓存修复及 USB 手机目录按需读取。设备来源依赖仍打开的资源管理器手机目录，识别失败时只看单图。手机原图不写入；当前本地测试版本仍为 0.3.12，未创建新 GitHub Release。最新测试结果以本轮执行记录为准，旧发布包哈希不代表重新构建后的产物。

> 状态快照：2026-08-16
> 用途：供下一位维护者或编程 Agent 在当前工作区直接接手。
> 事实优先级：实时 `git` / 文件元数据 / 测试结果 > 本报告 > 历史对话或旧结论。

本报告记录当前仓库、正式发布、本地测试版、已完成工作、验证证据、失败教训和下一步决策边界。开始修改前仍须阅读 `AGENTS.md`、`README.md`、`docs/ARCHITECTURE.md` 和 `docs/RELEASE.md`。

## 30 秒接手摘要

- 当前分支：`main`。
- `HEAD`、`origin/main` 和标签 `v0.3.11` 当前都指向：
  `e10cf2d25de624ca80ab7f57db9ec06203bbe062`。
- GitHub 正式发布版本仍是 `0.3.11`；不要把本地测试版误写成已经公开发布。
- 当前源码里的本地测试版本已升到 `0.3.12`，但尚未提交、推送、打标签或创建 GitHub Release。
- 写本报告前，唯一未提交的跟踪文件是 `src/Pixora/Pixora.csproj`；写完本报告后，`docs/HANDOFF.md` 也会成为预期的未提交改动。
- 本机已经生成并安装验证过 `0.3.12` 测试包；实际设置页显示：
  - `Pixora 0.3.12`
  - `程序更新时间：2026-08-09 22:56`
- 用户当前要求暂停其他工作并转移工作流。除非用户重新指定优先级，不要继续性能改造、发布或 GitHub 操作。
- 重要真实性边界：本轮没有落地新的性能优化代码。现有仓库本身已有缓存、虚拟化和目录增量更新等优化，但不要声称“性能优化全部完成”，也不要假定存在聊天中提过却未进入 Git 的类型或功能。

## Git 与远端精确状态

快照命令：

```powershell
git status --short --branch
git diff -- src\Pixora\Pixora.csproj
git rev-parse HEAD
git rev-parse origin/main
git rev-parse v0.3.11
```

快照结果：

```text
## main...origin/main
 M src/Pixora/Pixora.csproj
```

本报告落盘后，预期状态会增加：

```text
 M docs/HANDOFF.md
 M src/Pixora/Pixora.csproj
```

`src/Pixora/Pixora.csproj` 的唯一代码差异是四个版本字段：

```xml
<Version>0.3.12</Version>
<AssemblyVersion>0.3.12.0</AssemblyVersion>
<FileVersion>0.3.12.0</FileVersion>
<InformationalVersion>0.3.12</InformationalVersion>
```

对应的 Git 基线值均为 `0.3.11` / `0.3.11.0`。

远端：

```text
origin  https://github.com/OmoriRay/Pixora.git
```

不要自动提交、回退、推送或发布这两个未提交文件。是否把 `0.3.12` 变成正式版本，是需要用户明确决定的发布动作。

## 正式发布状态

当前公开 Release：

- 标签：`v0.3.11`
- 标题：`Pixora v0.3.11`
- 提交：`e10cf2d25de624ca80ab7f57db9ec06203bbe062`
- Release：<https://github.com/OmoriRay/Pixora/releases/tag/v0.3.11>
- 下载：<https://github.com/OmoriRay/Pixora/releases/download/v0.3.11/Pixora-win-x64.zip>
- 正式附件 SHA-256：
  `c1c49ca53451fc3a349931908bdfab7b560db1a8cf86dbea1fe8383adc58b132`
- 当前不是草稿，也不是预发布版本。

`README.md` 中“当前发布版本为 `0.3.11`”仍然正确。除非真正完成 `v0.3.12` 发布，不要提前改成正式发布状态，也不要覆盖 `v0.3.11` 的附件。

## 本地测试版 0.3.12

### 发布目录

仓库内忽略的发布目录：

```text
publish\Pixora-win-x64
publish\Pixora-win-x64.zip
```

当前本地发布物：

- `publish\Pixora-win-x64\Pixora.exe`
  - FileVersion：`0.3.12.0`
  - ProductVersion：`0.3.12+e10cf2d25de624ca80ab7f57db9ec06203bbe062`
  - 文件时间：`2026-08-09 22:56:59.094`
  - SHA-256：
    `D82135DEBAAAB28CEAF6ADA9D644265D89C802707E9CFF16E86D2D8C6B6FCCA1`
- `publish\Pixora-win-x64.zip`
  - 文件时间：`2026-08-09 22:57:02.173`
  - SHA-256：
    `912DBBF3BB7D1C72BFFDD60E3FC2DEA125EB273937AA4845382880F9B352247D`

SDK 会把源码修订号追加到 ProductVersion；设置页的 `GetAppVersion()` 会移除 `+` 后的构建元数据，所以用户看到的是 `Pixora 0.3.12`。

### 本机测试安装

用户指定的本机测试目录是：

```text
<本地安装目录>
```

这是机器本地状态，不是仓库或公开发布契约；不要把该路径复制到 README 或 Release 说明。

已核对安装目录里的 `Pixora.exe`：

- FileVersion：`0.3.12.0`
- 文件时间：`2026-08-09 22:56:59.094`
- SHA-256：
  `D82135DEBAAAB28CEAF6ADA9D644265D89C802707E9CFF16E86D2D8C6B6FCCA1`

它与 `publish\Pixora-win-x64\Pixora.exe` 的版本、时间和哈希完全一致。

本地视觉证据位于被 `.gitignore` 忽略的：

```text
test-output\installed-settings-version-final.png
```

截图来自实际安装版窗口，清晰显示：

```text
Pixora 0.3.12
程序更新时间：2026-08-09 22:56
```

验证结束后测试 Pixora 进程已关闭，临时快捷键验证备份已清除，原 `%LOCALAPPDATA%\Pixora\shortcuts.json` 已按原始 SHA-256 恢复。

## 本轮已完成的功能工作

### 1. 修复 Explorer 大图标视图回退为 Pixora 图标

实现位置：

- `src/Pixora/Services/FileAssociationService.cs`
- `src/Pixora/App.xaml.cs`
- `src/Pixora/Services/MediaFormatRegistry.cs`
- `tests/Pixora.SmokeTests/Program.cs`
- `docs/ARCHITECTURE.md`

关键行为：

- `Pixora.Image` ProgID 注册 Windows 照片缩略图处理器。
- 缩略图处理器类别：
  `{E357FCCD-A995-4576-B01F-234630154E96}`。
- Windows Photo Thumbnail Provider：
  `{C7657C4A-9F68-40FA-A4DF-96BC08EB3551}`。
- `Register()` 将处理器写入当前用户范围的 ProgID ShellEx 路径。
- `TryRepairRegistrationForCurrentExecutable()` 在启动时检查当前有效安装：
  - 程序路径失效时修复；
  - 当前安装缺少或使用错误缩略图处理器时补齐；
  - 另一份仍有效的安装不会被 Debug / 便携副本抢占；
  - 不修改受保护的 Windows `UserChoice`。
- 修改完成后调用 `SHChangeNotify(SHCNE_ASSOCCHANGED, ...)` 通知 Shell。
- 支持的静态图片扩展名统一来自 `MediaFormatRegistry`，避免文件关联和目录索引清单漂移。

对应提交：

```text
922aec4 fix: restore Explorer image thumbnails
```

### 2. 超大伪 PNG 仍显示图标的原因

曾检查过一个约 `4.76 GiB`、扩展名为 `.png` 的文件。文件头是 `II+`，实际为 BigTIFF，不是 PNG。Windows Shell 请求缩略图返回：

```text
0x8004B200 / WTS_E_FAILEDEXTRACTION
```

因此 Explorer 回退显示 Pixora 文件类型图标是 Shell 无法提取缩略图后的正常行为，不代表普通 JPG/PNG 的注册修复失效。不要把这个个例变成“所有超大图片都应强制显示缩略图”的需求；需要先确认真实容器、是否有安全预览层和 Shell 能否解码。

### 3. 设置页显示版本号和程序更新时间

实现位置：

- `src/Pixora/ShortcutSettingsWindow.xaml`
- `src/Pixora/ShortcutSettingsWindow.xaml.cs`
- `tests/Pixora.SmokeTests/Program.cs`

主要控件：

- `AppInfoPanel`
- `AppVersionText`
- `AppUpdatedText`

关键逻辑：

- `GetAppVersion()` 读取 `AssemblyInformationalVersionAttribute`，回退到 AssemblyVersion，并删除 `+commit` 构建元数据。
- `GetAppUpdatedTimeText()` 优先读取 `Environment.ProcessPath` 对应 EXE 的本地最后写入时间，格式为 `yyyy-MM-dd HH:mm`；进程路径不可用时回退程序集路径。
- XAML 无障碍名称分别是“当前版本”和“程序更新时间”。
- 信息卡片配色与“诊断与关于”分组一致，并适配深色、亮色和易读性开关。

实际运行版已完成 DPI 感知的离屏截图验证，不再显示旧的 `2026-08-06 03:56`。

### 4. 0.3.11 正式发布

已经完成：

- Release 构建。
- smoke tests。
- framework-dependent `win-x64` 发布包。
- Git 提交、推送和 `v0.3.11` 标签。
- GitHub Release 创建与附件上传。
- 从 GitHub 回下载附件并核对 SHA-256。

### 5. 0.3.12 本地测试部署

已经完成：

- 四个项目版本字段改为 `0.3.12`。
- 强制清理增量构建输出后重新发布。
- 重新生成 ZIP。
- Release 构建通过，0 警告、0 错误。
- 控制台式 smoke tests 通过。
- ZIP 完整性检查通过。
- 同步到本机测试目录。
- 发布版和安装版 EXE 的版本、时间、哈希一致。
- 从实际安装目录启动并完成设置页视觉验证。

这部分仍然只是本地未提交测试状态。

## 测试与验证方法

项目的 smoke test 是控制台入口，不是标准 xUnit 测试项目。正确命令：

```powershell
dotnet build Pixora.sln
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

不要只运行 `dotnet test` 并据此声称 smoke tests 已执行。

本轮覆盖或核对的相关回归包括：

- 当前有效安装缺少缩略图处理器时需要刷新注册。
- 错误处理器需要刷新。
- 完整注册不应每次启动重写。
- 另一份有效安装不能被当前 Debug / 便携副本覆盖。
- 设置窗口存在 `AppInfoPanel`、`AppVersionText`、`AppUpdatedText`。
- 用户可见版本号不包含 `+` 后的构建元数据。
- 版本和更新时间控件具有预期无障碍名称。
- 发布包 ZIP 可读取且包含预期文件。
- 发布版和安装版 EXE 哈希一致。

涉及 UI 改动时，可以使用本地忽略的截图工具或真实窗口截图，但不要提交 `test-output/`。

## 关键失败教训

### 增量发布会复用旧 EXE 时间

`publish.ps1` 会删除 `publish\Pixora-win-x64` 和 ZIP，但不会删除：

```text
src\Pixora\bin
src\Pixora\obj
tests\Pixora.SmokeTests\bin
tests\Pixora.SmokeTests\obj
```

也没有给 `dotnet publish` 传入强制非增量参数。因此只删发布目录并重新运行脚本，可能继续复用旧 EXE，造成设置页“程序更新时间”不变。

需要强制刷新 EXE 时，先清理项目 `bin/obj`，再发布。例如：

```powershell
Remove-Item src\Pixora\bin, src\Pixora\obj -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item tests\Pixora.SmokeTests\bin, tests\Pixora.SmokeTests\obj -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item publish\Pixora-win-x64, publish\Pixora-win-x64.zip -Recurse -Force -ErrorAction SilentlyContinue
.\publish.ps1 -Configuration Release -Runtime win-x64 -Zip
```

随后必须核对实际 EXE 的 FileVersion、LastWriteTime 和 SHA-256，而不是只看命令退出码。

### 文件扩展名不等于真实容器

超大或异常文件先读文件签名，再判断解码、Shell 缩略图或安全预览策略。不要尝试用 `ReadAllBytes` 读取数 GiB 文件；应只读取足够识别格式的文件头或使用流式工具。

### Git Bash 可能转换 `reg.exe` 参数

从 Git Bash 调用 Windows 注册表工具时，MSYS 路径转换可能破坏注册表路径。必要时设置：

```powershell
$env:MSYS2_ARG_CONV_EXCL = '*'
```

或直接使用 PowerShell / .NET Registry API。

### 应用级 XAML 资源的 smoke test 边界

窗口 smoke test 可能使用裸 `Application`，不会自动加载 `App.xaml`。需要在测试中可用的应用级资源应位于主题资源字典，并由测试显式加载；否则真实应用正常、smoke test 却会因资源缺失失败。

### UI 调整不要只凭代码猜测

设置页和其他 WPF UI 修改后应渲染 PNG 或启动真实窗口检查深色、亮色、窄窗口和高 DPI。`test-output/UiSnapshot` 是本地忽略的辅助目录，不应提交。

### 不要信任没有 Git 证据的状态报告

前序过程中曾出现“性能优化已全部落地、仓库干净、测试通过”的错误状态描述，但真实 Git 中没有对应性能改动。接手者必须用：

```powershell
git status --short --branch
git diff --stat
git diff
```

以及真实构建/测试结果重新确认，不要根据历史文字假定代码已经存在。不要凭空引入未在当前代码中出现的 `ScanMode`、LRU 实现或缓存限制。

## 暂停和未完成事项

### 1. 0.3.12 是否正式发布

尚未决定，也未获授权执行。当前缺少的正式发布动作包括：

- 决定并确认 Release 范围和说明。
- 把 `README.md` 的当前发布版本同步为 `0.3.12`。
- 更新本报告中的正式发布状态。
- 对最终待提交内容重新运行完整构建、smoke tests、`git diff --check` 和隐私扫描。
- 提交并推送。
- 创建并推送 `v0.3.12` 标签。
- 创建新的 GitHub Release，上传最终 ZIP。
- 从 GitHub 回下载附件并核对 SHA-256。

除非用户明确要求，不要执行这些外部写操作。

### 2. 性能优化请求已暂停

用户曾要求检查并实施性能优化，但当前没有新的性能优化差异进入工作区。仓库基线已有大目录后台补齐、缩略图虚拟化、缓存预算、低内存保护和目录事件增量更新；如果用户恢复该任务，必须从真实代码重新审计和测量，而不是沿用历史对话中未经证实的方案。

建议顺序：

1. 读 `docs/ARCHITECTURE.md` 中打开路径、缩略图虚拟化和缓存章节。
2. 检查 `ImageCatalog`、`MediaCatalogLoader`、`FolderChangeMonitor`、`ImageCache`、`BitmapSourceMemoryCache`、`ThumbnailImageLoader`、`ThumbnailDiskCache` 和 `MainWindow` 的真实调用链。
3. 先建立可复现基准，再决定优化项。
4. 每项改动单独验证，避免一次大改难以定位回归。

在用户当前“先不要管其他的”指示下，不应主动继续这项工作。

### 3. PR 状态

没有有效 PR。先前相关改动已经直接进入 `main`，功能分支与 `main` 没有差异，因此不能创建有意义的空 PR。不要为了制造 PR 而回退、重写或伪造差异。

## 下一位 Agent 的安全启动流程

第一步只读检查：

```powershell
git status --short --branch
git diff -- src\Pixora\Pixora.csproj docs\HANDOFF.md
git log -5 --oneline --decorate
gh release view v0.3.11 --repo OmoriRay/Pixora
```

第二步阅读：

1. `AGENTS.md`
2. `README.md`
3. `docs/HANDOFF.md`
4. `docs/ARCHITECTURE.md`
5. `docs/RELEASE.md`

第三步向用户确认当前目标，只在目标明确后行动。推荐先复述：

- 正式版本是 `0.3.11`。
- 本地未提交测试版本是 `0.3.12`。
- 当前预期未提交文件是版本文件和本交接报告。
- 其他工作处于暂停状态。

如果只是继续本地测试，不要推送或发布。

如果用户明确要求发布 `0.3.12`，按照 `docs/RELEASE.md` 从最终清理、构建和测试重新开始，不要直接上传当前本地 ZIP。

## 关键文件和入口

- `AGENTS.md`：维护约束、隐私、测试和发布基本规则。
- `src/Pixora/Pixora.csproj`：当前未提交的 `0.3.12` 版本字段。
- `src/Pixora/App.xaml.cs`：启动和文件关联修复入口。
- `src/Pixora/AppInfo.cs`：品牌、数据目录、ProgID 和图标路径。
- `src/Pixora/Services/FileAssociationService.cs`：文件关联、缩略图处理器和默认应用逻辑。
- `src/Pixora/Services/MediaFormatRegistry.cs`：静态图片与视频扩展名的统一来源。
- `src/Pixora/ShortcutSettingsWindow.xaml`：设置页版本信息卡片。
- `src/Pixora/ShortcutSettingsWindow.xaml.cs`：版本号和程序更新时间计算。
- `src/Pixora/MainWindow.xaml.cs`：主窗口大部分用户工作流。
- `tests/Pixora.SmokeTests/Program.cs`：控制台式回归测试入口。
- `publish.ps1`：发布和 ZIP 生成；注意它不清理项目 `bin/obj`。
- `docs/ARCHITECTURE.md`：真实架构、性能基线和修改边界。
- `docs/RELEASE.md`：正式发布流程和检查表。

## 禁止自动执行的动作

未获得用户当前明确授权时，不要：

- 提交或回退 `0.3.12` 版本差异。
- 推送 `main`。
- 创建或移动 Git 标签。
- 创建、覆盖或删除 GitHub Release。
- 覆盖 `v0.3.11` 附件。
- 删除用户本地设置、测试图片或未提交文件。
- 修改 Windows `UserChoice`。
- 提交 `bin/`、`obj/`、`publish/`、`test-output/`、`.artifacts*/` 或 `tools/SetUserFTA.exe`。
- 声称性能优化已经完成。

## 可直接交给下一位 Agent 的开场提示

```text
请先完整阅读 AGENTS.md、docs/HANDOFF.md、README.md、docs/ARCHITECTURE.md 和 docs/RELEASE.md，然后只读检查 git status、git diff、HEAD/origin/main/v0.3.11 以及 GitHub Release v0.3.11。当前正式发布是 0.3.11；src/Pixora/Pixora.csproj 中未提交的 0.3.12 只是已构建并在本机验证过的测试版，尚未提交、推送、打标签或发布。docs/HANDOFF.md 是本次交接更新后的预期未提交文档。先向我报告你确认到的真实状态，不要自动提交、回退、推送、发布或继续性能优化；等我指定下一项任务。
```

## 报告维护规则

本报告是状态快照，不是永久真理。下一位维护者完成以下任一动作后，应立即更新本文件：

- 提交或回退 `0.3.12`。
- 推送新提交。
- 创建新标签或 GitHub Release。
- 重新生成发布包导致哈希变化。
- 改变本机测试目录或完成新的人工回归。
- 恢复性能优化任务并产生真实代码差异。
