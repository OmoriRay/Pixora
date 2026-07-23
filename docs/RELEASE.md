# Pixora 发布说明

本文记录从本地检查到 GitHub Release 的完整流程。发布前请同时阅读 `AGENTS.md` 和 `docs\ARCHITECTURE.md`。

## 发布目标

当前默认发布目标：

- 应用名：`Pixora`
- 解决方案：`Pixora.sln`
- 主项目：`src\Pixora\Pixora.csproj`
- Target Framework：`net9.0-windows`
- Runtime：`win-x64`
- 默认发布目录：`publish\Pixora-win-x64`
- 默认压缩包：`publish\Pixora-win-x64.zip`
- 许可证：MIT License

公开运行环境应分行明确表述为：

- Windows 10 x64：最低支持 22H2
- Windows 11 x64
- 需要 .NET Desktop Runtime 9 x64

不要再写成“支持 Windows 10 22H2、Windows 11 x64”或类似并列版本列表，以免被理解为 Windows 10 仅支持某个特定版本，或误以为 Windows 10 不要求 x64。

已经公开发布的 Release 默认保持不可变。较多功能、行为或解码能力改动必须升级补丁版本，例如从 `0.3.7` 升级到 `0.3.8`，重新构建、测试、打标签并创建新 Release；不要直接覆盖旧版本附件。只有同版本构建损坏、附件缺失等明确的发布事故，并且用户明确要求覆盖时，才可以替换现有附件，同时应在 Release 正文记录替换日期和原因。

Release 配置关闭 PDB：

```xml
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
```

原因是 PDB 可能包含本机源码路径。公开发布包里不需要这些调试符号。

## 版本号位置

发布前同步检查：

- `src\Pixora\Pixora.csproj`
  - `<Version>`
  - `<AssemblyVersion>`
  - `<FileVersion>`
  - `<InformationalVersion>`
- `README.md`
  - `当前发布版本：`
- Git 标签
  - 推荐格式：`vX.Y.Z`
- GitHub Release 标题
  - 推荐格式：`Pixora vX.Y.Z`

如果只改文档，不需要升级版本号。功能、行为或发布包变化才需要考虑升版本。

## 发布前清理

确认工作区只包含应该提交的源码和文档：

```powershell
git status --short --branch
```

构建产物和本地测试产物不应提交：

```text
bin/
obj/
publish/
test-output/
.artifacts*/
```

可选外部工具不能提交：

```text
tools\SetUserFTA.exe
```

## 隐私扫描

扫描具体 AI 工具或模型名。下面命令使用占位符，执行前把关键词替换成你本机需要排查的内容；不要把真实关键词提交进仓库。

```powershell
rg -n -i "<具体AI工具或模型名>|<具体AI平台名>" . -g "!**/bin/**" -g "!**/obj/**" -g "!publish/**" -g "!test-output/**" -g "!.git/**"
```

扫描本机路径、用户名、令牌和私有测试素材名：

```powershell
rg -n -i "<本机用户名>|<私有绝对路径>|<私有样本目录>|<访问令牌片段>" . -g "!**/bin/**" -g "!**/obj/**" -g "!publish/**" -g "!test-output/**" -g "!.git/**"
```

处理原则：

- README 中可以保留泛化说明：“本项目由 AI 辅助编程完成。”
- 不要保留具体 AI 工具、模型、会话、账号或自动化平台信息。
- 不要保留本机绝对路径、用户目录、私有样本目录或访问令牌。
- 如果扫描命中代码里的正常英文缩写，确认不是隐私信息后可以保留。

## 构建和测试

先构建：

```powershell
dotnet build Pixora.sln
```

运行 smoke test：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

涉及解码、缩略图、视频封面、大目录性能时，建议加真实素材目录：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj -- --media-folder "D:\Samples" --sample-count 50 --video-sample-count 5
```

如果测试失败，先读英文报错含义，再定位对应服务。不要为了发布跳过失败测试，除非明确记录失败原因和风险。

## 手动回归检查

发布前建议至少手动检查这些路径：

- 双击打开一张普通 JPG/PNG。
- 从命令行传入图片路径。
- 从命令行传入目录路径。
- 在大目录中双击某一张图，确认先显示当前图，再补齐列表。
- 上一张/下一张、适应窗口、原始大小、缩放、旋转。
- 缩放比例提示会自动淡出；百分比模式以图片或安全预览为基准，相对倍数模式以适应窗口为 `1.00×`，两种设置重启后仍保留。
- 缩略图栏显示/隐藏、单列/双列。
- GIF/APNG 动图播放、暂停、重新播放。
- 收藏、取消收藏、只看收藏、退出收藏。
- 设置窗口大小拉大后关闭，再重新打开仍能恢复。
- 快捷键编辑、冲突提示、恢复默认。
- 裁剪、圆形裁剪、保存裁剪。
- 单图压缩 JPEG/PNG。
- 批量压缩目录，确认动图跳过、失败项有提示、输出目录有日志。
- 视频文件显示封面，并可另存视频封面。
- 静态图片右键显示“设为桌面壁纸”，GIF/APNG/视频不显示。
- 文件关联注册后，资源管理器中的图片文件类型图标是 Pixora 图片图标。
- 超大 JPEG/PNG 和带安全金字塔层的 BigTIFF 能显示安全预览，信息栏显示真实打开用时；快速小目录索引不会显示误导性的 `0.0 秒`。

## 生成发布包

默认发布：

```powershell
.\publish.ps1
```

生成 zip：

```powershell
.\publish.ps1 -Zip
```

自包含运行时：

```powershell
.\publish.ps1 -SelfContained -Zip
```

默认输出：

```text
publish\Pixora-win-x64
publish\Pixora-win-x64.zip
```

## 发布包检查

检查发布目录：

```powershell
Get-ChildItem publish\Pixora-win-x64
```

必须有：

- `Pixora.exe`
- `LICENSE`
- `Assets\PixoraImage.ico`
- `tools\batch-compress.ps1`
- `tools\batch-compress-README.md`
- 运行所需的 `.dll` 和 `.deps.json` / `.runtimeconfig.json`

不应有：

- `Pixora.pdb`
- `SetUserFTA.exe`
- 本机测试图片、私有样本、日志或临时文件

可用命令检查：

```powershell
Test-Path publish\Pixora-win-x64\LICENSE
Test-Path publish\Pixora-win-x64\Pixora.pdb
Test-Path publish\Pixora-win-x64\tools\SetUserFTA.exe
```

预期：

- `LICENSE` 返回 `True`。
- `Pixora.pdb` 返回 `False`。
- `tools\SetUserFTA.exe` 在正式开源包中返回 `False`。

## 本地试运行发布包

直接运行：

```powershell
.\publish\Pixora-win-x64\Pixora.exe
```

传入测试图片或目录：

```powershell
.\publish\Pixora-win-x64\Pixora.exe "D:\Pictures"
```

注意：如果是非 self-contained 发布，测试机器需要有对应 .NET 运行时。

## Git 提交

常规流程：

```powershell
git status --short --branch
git add README.md AGENTS.md docs src tests tools LICENSE publish.ps1 .gitignore
git status --short
git commit -m "..."
git push origin main
```

只提交实际修改过的文件。不要因为命令示例里列了某个路径就机械添加无关文件。

如果需要打标签：

```powershell
git tag v0.3.0
git push origin v0.3.0
```

如果标签已经存在，不要强推覆盖，先确认 GitHub Release 是否已经发布。

## GitHub Release

创建 Release 示例：

```powershell
gh release create v0.3.0 publish\Pixora-win-x64.zip --title "Pixora v0.3.0" --notes "Pixora v0.3.0"
```

如果 Release 已存在，只更新附件：

```powershell
gh release upload v0.3.0 publish\Pixora-win-x64.zip --clobber
```

发布后检查：

```powershell
gh release view v0.3.0 --web
```

确认内容：

- Release 标题正确。
- 附件是最新 `Pixora-win-x64.zip`。
- 附件名不含本机路径。
- Release 说明没有隐私信息或具体 AI 工具信息。

## 默认应用说明

Pixora 设置页里有两个层级：

- “关联常见图片格式”：把 Pixora 加入 Windows 打开方式列表。
- “关联并设为默认应用”：先注册，再尝试官方静默路径；如果系统不允许，则打开 Windows 默认应用设置页。

现代 Windows 通常禁止普通应用静默设置默认应用，这是系统保护机制，不是发布包缺文件。

“高级一键默认”只在用户自行提供 `SetUserFTA.exe` 时可用。这个工具不能进仓库，也不应作为正式发布包内置能力宣传。

## 用户删除本地项目后的恢复

只要代码已经推送到 GitHub，用户删除本地工作目录后仍可重新下载：

```powershell
git clone https://github.com/OmoriRay/Pixora.git
```

下载 Release 包则从 GitHub Releases 页面获取 zip。GitHub 可以保存源码和发布附件，但它不是完整备份策略；未提交的本地文件、未上传的 Release 包和用户本机设置不会自动保存。

## 发布前最终检查表

- `README.md` 版本号正确。
- `.csproj` 版本号正确。
- `LICENSE` 存在且发布包包含它。
- `dotnet build Pixora.sln` 通过。
- smoke test 通过。
- 真实样本测试按需通过。
- 隐私扫描无具体 AI 工具、模型、账号、令牌、本机路径。
- `git status --short --branch` 只显示预期改动，或提交后干净。
- `publish\Pixora-win-x64.zip` 是最新生成。
- 发布包不包含 PDB。
- 发布包不包含 `SetUserFTA.exe`。
- Git 标签和 GitHub Release 对应同一个提交。
