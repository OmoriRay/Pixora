# Pixora

当前版本：`0.3.3`

Pixora 是一个 Windows WPF 看图工具，支持图片/视频混合目录浏览、缩略图栏、快捷键、收藏、显示级旋转、排序切换、记住上次目录、裁剪、圆形裁剪、图片压缩、批量压缩、设置桌面壁纸、批量删除到回收站和视频封面另存。缩略图磁盘缓存支持设置容量上限、自动淘汰和手动清理。

本项目由 AI 辅助编程完成。

## 环境要求

运行发布包：

- Windows 10 22H2 或 Windows 11，x64
- .NET Desktop Runtime 9 x64

从源码构建：

- .NET 9 SDK

项目使用 WPF 和 `Magick.NET-Q16-AnyCPU`，目标框架为 `net9.0-windows`。

Windows 7、Windows 8.1 和 32 位 Windows 不在当前发布包的支持范围内，不建议下载或作为兼容性测试目标。

## 运行

```powershell
dotnet run --project src\Pixora\Pixora.csproj
```

也可以传入文件或文件夹路径：

```powershell
dotnet run --project src\Pixora\Pixora.csproj -- "D:\Pictures"
```

## 测试

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

该 smoke test 会覆盖目录排序与取消、常见图片解码、GIF/HDR/AVIF、视频缩略图、媒体格式注册、内存与缩略图缓存、日志滚动、快捷键、设置持久化、裁剪数学、压缩和渲染控件。

## 接手维护

- `AGENTS.md`：给后续接手者和 AI 编程助手的工程约定。
- `docs\ARCHITECTURE.md`：工程结构、核心流程、服务职责、设置文件和已知技术债。
- `docs\RELEASE.md`：版本、测试、隐私扫描、发布包检查和 GitHub Release 流程。

## 发布

```powershell
.\publish.ps1
```

默认输出到 `publish\Pixora-win-x64`。如需自包含运行时：

```powershell
.\publish.ps1 -SelfContained
```

如需生成可直接上传到 GitHub Release 的压缩包：

```powershell
.\publish.ps1 -Zip
```

## 已知限制

- 批量压缩目前只处理静态图片；GIF、APNG、WebP 等动图会在预扫描后跳过，避免破坏动画帧。
- 视频文件只作为目录浏览项显示封面预览，不提供视频播放、裁剪或压缩。
- 视频封面来自 Windows Shell 缩略图；如果系统没有生成对应缩略图，Pixora 会显示内置占位封面。
- 缩略图磁盘缓存可在设置中开启，可选择容量上限；缓存达到上限后会按最近访问时间淘汰，设置页也可手动清理。首次超过上限后的少量新缩略图会在下一轮后台清理中回收。
- 文件关联写入当前用户注册表项；如果移动了整个 Pixora 文件夹，先从新位置手动启动一次 Pixora，它会在不更改默认应用选择的前提下更新旧的程序路径。如果系统策略限制注册表写入，关联操作可能失败。
- 解码超大图片和超多帧动图时有保护阈值，超过阈值会拒绝加载或只显示静态预览。

## 批量压缩

软件右键菜单里的“批量压缩图片”会打开可视化批量压缩窗口，支持选择文件/目录、输出目录、质量、最大宽高、格式、子目录和取消任务；窗口大小、位置和最大化状态会自动记住。

设置页里可以开启“启动时自动打开上次目录”。即使不开启，手动“打开目录”也会默认从上次成功打开的位置开始。

`tools` 目录里的脚本仍保留作为备用：

```text
tools\batch-compress.ps1
```

脚本详细用法见：

```text
tools\batch-compress-README.md
```

脚本依赖 ImageMagick CLI 的 `magick` 命令。常用示例：

```powershell
.\tools\batch-compress.ps1 -InputPath "D:\Pictures" -Quality 82 -MaxWidth 1920 -MaxHeight 1920 -Format jpg
```

## 主要目录

- `src\Pixora`：主程序源码。
- `src\Pixora\Services`：图片加载、压缩、目录索引、快捷键、设置和日志等服务。
- `src\Pixora\Controls`：自定义图片渲染控件。
- `tests\Pixora.SmokeTests`：控制台式冒烟测试。
- `test-images`：测试样本图片。
- `tools`：外部辅助脚本。

## 文件关联和默认应用

设置页里的“关联常见图片格式”会把 Pixora 加入 Windows 的“打开方式”列表。“关联并设为默认应用”会先尝试官方静默设置；当前 Windows 不支持时，会打开 Pixora 的默认应用设置页让用户确认。

“高级一键默认”需要用户自行提供外部工具 `SetUserFTA.exe`，并放到发布目录的 `tools` 文件夹中。该模式会在明确确认后批量执行外部工具，把支持的图片扩展名设为 `Pixora.Image`。这是非官方高级方案，Pixora 不内置该工具。

## 本地数据

设置和错误日志写入：

```text
%LOCALAPPDATA%\Pixora
```

其中包括 `viewer-settings.json`、`shortcuts.json`、`favorites.json` 和 `error.log`。旧版 `%LOCALAPPDATA%\Pic-O` 和更早的 `%LOCALAPPDATA%\PureView` 中的设置、快捷键、收藏和批量压缩设置会在首次启动时迁移。

## 许可证

本项目使用 MIT License 开源，详见 `LICENSE`。第三方依赖遵循其各自许可证。
