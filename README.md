<p align="center">
  <img src="src/Pixora/Assets/PixoraIcon.png" width="112" alt="Pixora 图标">
</p>

<h1 align="center">Pixora</h1>

<p align="center">面向 Windows 10/11 x64 的轻量图片查看器</p>

<p align="center">
  <a href="https://github.com/OmoriRay/Pixora/releases/latest">下载</a> ·
  <a href="#主要功能">功能</a> ·
  <a href="docs/ARCHITECTURE.md">架构</a> ·
  <a href="docs/RELEASE.md">发布</a>
</p>

当前发布版本为 `0.3.8`，`main` 分支可能包含尚未发布的改进。

Pixora 支持图片与视频混合目录浏览、快速搜索、缩略图、收藏、基础编辑和超大图片安全预览。本项目由 AI 辅助编程完成。

## 主要功能

| 类别 | 能力 |
| --- | --- |
| 浏览 | 常见图片格式、视频封面、缩放、旋转、全屏和排序 |
| 大目录 | 后台扫描、虚拟化缩略图、目录变化自动刷新 |
| 查找 | 使用 `Ctrl+K` 按序号或文件名快速定位 |
| 编辑 | 裁剪、圆形裁剪、单图压缩、批量压缩和设置壁纸 |
| 性能 | 内存与磁盘缓存、低内存保护、邻图预加载 |
| 超大图 | JPEG/PNG 和带预览层的 BigTIFF 安全预览 |
| 系统 | 深色/亮色主题、快捷键、收藏、窗口复用和 Windows 文件关联 |

## main 分支当前改进

- 超大 JPEG/PNG 安全预览，以及带缩略图或金字塔层的 BigTIFF 安全预览。
- 显示真实图片打开耗时，避免快速目录索引覆盖加载信息。
- 新增可自动淡出的缩放比例提示，支持百分比和相对“适应窗口”倍数。
- 新增可即时预览并持久化的深色、亮色主题。

## 环境要求

运行发布包：

- Windows 10 x64：最低支持 22H2
- Windows 11 x64
- .NET Desktop Runtime 9 x64

从源码构建需要 .NET 9 SDK。项目目标框架为 `net9.0-windows`，主要图片依赖为 `Magick.NET-Q16-AnyCPU`。

Windows 7、Windows 8.1 和 32 位 Windows 不在支持范围内。

## 运行

```powershell
dotnet run --project src\Pixora\Pixora.csproj
```

也可以传入图片或目录：

```powershell
dotnet run --project src\Pixora\Pixora.csproj -- "D:\Pictures"
```

## 测试

```powershell
dotnet build Pixora.sln
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

Smoke test 覆盖图片解码、超大图策略、视频封面、目录索引、快速搜索、缓存、设置、快捷键、裁剪、压缩、渲染和大目录性能。

需要测试真实素材目录时：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj -- --media-folder "D:\Samples" --sample-count 50 --video-sample-count 5
```

## 发布

```powershell
.\publish.ps1 -Zip
```

默认生成：

```text
publish\Pixora-win-x64
publish\Pixora-win-x64.zip
```

默认发布包依赖 .NET Desktop Runtime 9 x64；使用 `-SelfContained` 可生成自包含版本。完整检查流程见 [docs/RELEASE.md](docs/RELEASE.md)。

## 已知限制

- 视频仅显示系统提供的封面，不支持播放、裁剪或压缩。
- 批量压缩只处理静态图片，动图会被跳过。
- 超大图片可能只加载安全预览；需要完整原始像素的裁剪、压缩和壁纸功能会停用。
- BigTIFF 只使用已有缩略图或金字塔层，不支持按瓦片读取和任意深度缩放。
- Windows 10/11 通常不允许普通应用静默修改默认应用。

详细解码边界和实现说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 文件关联

- “关联常见图片格式”会将 Pixora 添加到 Windows 的“打开方式”列表。
- “关联并设为默认应用”会尝试系统支持的方式；现代 Windows 通常需要用户在设置页确认。
- “高级一键默认”依赖用户自行提供的 `SetUserFTA.exe`，项目和正式发布包不内置该工具。

## 本地数据

设置、收藏、快捷键、缓存和错误日志保存在：

```text
%LOCALAPPDATA%\Pixora
```

启动时会尽力迁移旧版 `%LOCALAPPDATA%\Pic-O` 和 `%LOCALAPPDATA%\PureView` 中的用户数据。

## 项目结构

- `src\Pixora`：WPF 主程序。
- `src\Pixora\Services`：图片加载、目录索引、缓存、设置和文件关联等服务。
- `tests\Pixora.SmokeTests`：控制台式冒烟测试。
- `test-images`：内置测试样本。
- `tools`：批量压缩辅助脚本。

维护前请阅读 [AGENTS.md](AGENTS.md)、[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) 和 [docs/RELEASE.md](docs/RELEASE.md)。

## 许可证

Pixora 使用 MIT License，详见 [LICENSE](LICENSE)。
