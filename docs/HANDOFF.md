# Pixora 交接手册

这份手册记录当前可交接状态和下一次维护的最短路径。项目约定仍以 `AGENTS.md`、`README.md`、`docs/ARCHITECTURE.md` 和 `docs/RELEASE.md` 为准。

## 当前状态

- 当前发布版本：`0.3.9`
- 发布标签：`v0.3.9`
- 发布基线提交：`v0.3.9` 标签所指向的提交。
- `main` 的发布代码已与远端同步；接手时仍须先检查工作区状态。
- 正式下载包：GitHub Release `v0.3.9` 中的 `Pixora-win-x64.zip`。
- 图片解码依赖 `Magick.NET-Q16-AnyCPU` `14.15.0`；主项目和 smoke test 项目必须保持同一版本，否则会触发 `NU1605` 包降级错误。
- `publish.ps1` 会显式检查 `dotnet publish` 的 `$LASTEXITCODE`。Windows PowerShell 5.1 下 `$ErrorActionPreference` 不拦截原生命令失败，这个检查不能删。
- 默认外观仍为深色；设置页支持深色/亮色切换，选择会即时预览并保存到 `viewer-settings.json`。
- 设置页搜索功能已移除；快速搜索浮层（`Ctrl+K`）仍保留，用于序号跳转或文件名查找。

## 关键入口

- `src/Pixora/App.xaml`：全局控件样式和主题资源入口。
- `src/Pixora/Themes/Theme.Dark.xaml`、`Theme.Light.xaml`：两套语义颜色资源。
- `src/Pixora/Services/ThemeManager.cs`：运行期主题切换。
- `src/Pixora/Services/ViewerSettings.cs`：主题和主窗口设置持久化。
- `src/Pixora/MainWindow.xaml` / `.xaml.cs`：图片打开、拖放、导航、缩放、平移、缩略图和快捷键工作流。
- `tests/Pixora.SmokeTests/Program.cs`：构建后优先运行的回归入口。

## 接手检查

```powershell
git status --short --branch
dotnet build Pixora.sln
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

涉及图片解码、视频封面、缩略图或大目录时，再按 `AGENTS.md` 使用外部样本目录参数运行 smoke test。涉及 UI 时，重点检查亮色/深色切换、设置窗口、快捷键、缩略图单列/双列、压缩窗口和动图限制。

## 发布流程

1. 先完整阅读 `AGENTS.md`、`README.md`、`docs/ARCHITECTURE.md` 和 `docs/RELEASE.md`。
2. 检查 `git status --short --branch` 和完整差异，确认没有混入无关文件。
3. 更新 `src/Pixora/Pixora.csproj` 的四个版本字段和 `README.md` 当前版本。
4. 运行构建、smoke test、`git diff --check` 和隐私扫描。
5. 使用 `.\publish.ps1 -Zip` 生成 `publish/Pixora-win-x64.zip`，检查许可证、PDB 和外部工具内容。
6. 提交、推送 `main`，创建对应的 `vX.Y.Z` 标签和 GitHub Release，上传 zip。
7. 从 Release 重新下载附件并核对 SHA-256。

不要提交 `bin/`、`obj/`、`publish/`、`test-output/`、`.artifacts*/` 或 `tools/SetUserFTA.exe`。不要覆盖已有 Release；功能改动应递增补丁版本。

## 清理规则

以下目录均为可重建产物，空间紧张时可删除：

- `.artifacts*/`
- `.vs/`
- `bin/`、`obj/`
- `publish/`
- `test-output/`

清理后重新运行构建或发布脚本即可恢复。不要删除 `test-images/`、源码、`.git/`、`.agents/` 或用户数据目录；发布包和用户安装目录互不替代。

## 未完成事项

- 当前自动测试以服务、渲染和窗口初始化 smoke test 为主，仍不是完整 UI 自动化。
- 本地交接前未启动桌面窗口做人工视觉回归；需要视觉确认时，按用户授权再启动程序检查。
