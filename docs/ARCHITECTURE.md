# Pixora 架构说明

本文面向后续维护者，目标是让人或 AI 编程助手能快速理解工程结构、运行流程和修改边界。

## 项目定位

Pixora 是一个 Windows WPF 图片查看器，目标框架是 `net9.0-windows`。它的重点不是图片管理库，而是轻量打开、目录浏览、缩略图预览、快捷键、收藏、裁剪、压缩、批量压缩、壁纸设置和 Windows 文件关联。

项目目录、解决方案、命名空间、程序集名和用户可见品牌已经统一为 `Pixora`。旧名称 `Pic-O` 和 `PureView` 仅作为历史兼容名保留，主要用于本地数据迁移和历史记录说明。

## 工程结构

```text
Pixora.sln
src\Pixora
  App.xaml
  App.xaml.cs
  app.manifest
  AppInfo.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  ShortcutSettingsWindow.xaml
  ShortcutSettingsWindow.xaml.cs
  CompressImageWindow.xaml
  CompressImageWindow.xaml.cs
  BatchCompressWindow.xaml
  BatchCompressWindow.xaml.cs
  BatchDeletePreviewWindow.xaml
  BatchDeletePreviewWindow.xaml.cs
  Assets\
  Controls\
  Models\
  Services\
tests\Pixora.SmokeTests
test-images
tools
docs
```

主要目录职责：

- `src\Pixora`：WPF 应用主体。
- `src\Pixora\Controls`：自定义渲染控件，目前核心是 `BitmapViewer`。
- `src\Pixora\Models`：轻量模型和枚举，例如图片文档、动画帧、快捷键动作。
- `src\Pixora\Services`：图片加载、目录索引、目录变更监听、快速搜索索引与输入状态、缓存策略、原子 JSON 持久化、设置、快捷键、文件关联、收藏、压缩、错误日志等可测试逻辑。
- `tests\Pixora.SmokeTests`：控制台式 smoke test，不是完整 UI 自动化，但覆盖了核心服务和渲染回归。
- `test-images`：内置小样本，测试启动时会生成部分动态图、HDR、AVIF 或输出文件。
- `tools`：辅助脚本。`SetUserFTA.exe` 是可选外部工具，不能进仓库。

## 启动流程

入口是 WPF 的 `App`。

1. `App.OnStartup` 调用 `AppInfo.EnsureLocalDataMigrated()`。
2. `FileAssociationService.TryRepairRegistrationForCurrentExecutable()` 只在已有 `Pixora.Image` 打开命令指向已不存在的程序文件时更新为当前路径；仍然有效的安装副本不会被 Debug 或其他副本覆盖，也不会修改 Windows 默认应用选择。
3. 注册全局异常处理：`DispatcherUnhandledException`、`UnhandledException`、`UnobservedTaskException`。
4. 根据设置初始化单实例协调；第二个进程优先把启动路径转发给已有窗口。
5. 主窗口 `MainWindow` 初始化设置、快捷键、收藏、缓存和计时器。
6. `Window_Loaded` 处理启动路径；没有路径时，根据设置决定是否打开上次目录。
7. 打开文件或目录后，主窗口更新目录索引、图片文档、缩略图栏、标题、状态信息和命令可用性。

应用默认使用当前用户范围的命名管道和互斥量实现单实例。第二次从资源管理器双击图片时，新进程把路径转发给现有主窗口并退出；显式“新窗口打开”会携带 `--new-window` 参数绕过复用。

异常日志写入：

```text
%LOCALAPPDATA%\Pixora\error.log
```

## 品牌和路径集中配置

`src\Pixora\AppInfo.cs` 是品牌、数据目录和文件关联的集中配置点：

- `Name = "Pixora"`：窗口标题、注册应用名、消息框标题。
- `DataFolderName = "Pixora"`：当前本地数据目录。
- `PreviousDataFolderNames = ["Pic-O", "PureView"]`：旧数据目录，用于迁移。
- `FileAssociationProgId = "Pixora.Image"`：Windows 文件关联 ProgId。
- `FileTypeDisplayName = "Pixora 图片"`：资源管理器里显示的文件类型名。
- `Description = "Pixora 图片查看器"`：注册应用描述。
- `FileTypeIconRelativePath = @"Assets\PixoraImage.ico"`：文件类型图标。
- `WallpaperFilePrefix`：生成壁纸临时文件名前缀。
- `BatchCompressLogPrefix`：批量压缩日志文件名前缀。

改品牌、默认应用、发布包名称或数据目录时，先从这里开始，再同步 `.csproj`、`publish.ps1`、README 和发布说明。

## 主窗口职责

`MainWindow.xaml.cs` 目前承担了大量用户工作流，是项目最大的文件。它负责：

- 打开文件、文件夹和命令行路径。
- 在大目录中先显示当前文件，再后台补齐完整目录索引。
- 加载图片、视频封面、预览图和完整图。
- 管理缩放、平移、适应窗口、原始大小、旋转和全屏。
- 缩放时显示自动淡出的比例提示；百分比相对当前图片或安全预览，相对倍数以“适应窗口”为 `1.00×`，两种模式均可持久化选择。
- 播放 GIF/APNG 等动图帧。
- 显示和异步加载缩略图栏；快速滚动时取消过期视口任务，停稳后只恢复当前可见区域及缓冲行的解码。
- 显示独立的快速搜索浮层，并按图片序号或文件名定位目录项。
- 文件名搜索时仅在缩略图栏可见的情况下建立临时匹配行并加载可见结果；完整目录集合保持不变。零匹配时只更新搜索框反馈，右侧继续保留上一次有效缩略图集合、标题和滚动位置；隐藏缩略图栏时延迟恢复行布局，避免无效重建。
- 收藏、收藏视图和收藏状态同步。
- 快捷键分发。
- 裁剪、圆形裁剪、保存裁剪。
- 单图压缩和批量压缩入口。
- 批量删除当前目录媒体到回收站。
- 设置桌面壁纸。
- 文件信息、真实图片打开耗时、边界提示和操作通知；快速完成的小目录补齐不会用 `0.0 秒` 通知覆盖图片加载反馈。

维护建议：

- 小修小补可以直接在 `MainWindow` 内按现有模式做。
- 如果新增的是可独立测试的逻辑，优先放进 `Services`，再由主窗口调用。
- 不要把耗时 IO 或解码放到 UI 线程；现有代码大量使用 `Task.Run` 和 `CancellationTokenSource`，新逻辑应保持这个模式。
- 对当前图片路径的异步回调，必须检查取消令牌和当前路径是否仍匹配，避免用户快速切图后旧任务覆盖新 UI。

## 打开路径和大目录策略

`OpenPathAsync` 是打开入口。当前策略大致如下：

- 如果传入目录，`MediaCatalogLoader` 会在后台枚举该目录里的支持媒体，支持取消，排序后打开第一项。
- 如果传入文件，先用 `ImageCatalog.LoadSingleFile` 建一个只包含当前文件的占位目录，立即加载当前图。
- 随后 `StartCatalogCompletion` 通过 `MediaCatalogLoader` 在后台扫描同目录并排序。
- 后台扫描完成后，如果用户仍停留在同一张图，并且当前目录仍是单文件占位，则用完整目录替换占位目录。

这个设计是为了改善“在很大文件夹中双击某一张图”的启动体验。后续优化大目录性能时，不要退回到必须先完整扫描目录才能显示图片的流程。

`MediaCatalogLoader` 负责把目录枚举移出 UI 线程并传递取消令牌；`ImageCatalog` 只枚举当前目录，不递归。批量删除和批量压缩才会根据用户选项涉及子目录。

目录枚举会按批次报告已发现的支持媒体数量。直接打开目录时主窗口显示进度和取消按钮；直接打开单文件时先显示当前文件，再用非阻塞状态条补齐目录。`FolderChangeMonitor` 封装 `FileSystemWatcher` 并合并短时间内的外部新增、删除、重命名和修改事件；普通批次由 `ImageCatalog.ApplyPathChanges` 一次性增量更新和排序，监听缓冲区溢出、异常或超大事件批次才回退完整目录扫描。按修改时间或文件大小排序时，`ImageCatalog` 为每个路径缓存一次 `FileInfo` 元数据；重复排序复用快照，目录事件只让受影响路径失效。

主图邻图预加载会记录最近一次导航方向。根据可用内存，前向半径从低配的 2–3 张提高到 16 GB 的 4 张、32 GB 的 6 张和 64 GB 及以上的 8 张；高配档位保留反方向 1–2 张兜底。缩略图解码并发从低配的 1–2 提高到最多 6，同时受 `Environment.ProcessorCount` 限制。初次打开或目录刷新时仍采用双向邻图；低内存保护触发时不会执行后台主图预加载。

## 媒体格式

`MediaFormatRegistry` 集中维护静态图片、视频和轻量动图判断的扩展名；`ImageCatalog` 与 `FileAssociationService` 都从这里读取，避免出现格式清单漂移。

静态/图片扩展名包括：

```text
.jpg .jpeg .jpe .jfif .png .apng .bmp .gif .webp .tif .tiff .ico .cur .heic .heif .avif .avifs .jxr .wdp .hdp .hdr
```

视频扩展名包括：

```text
.mp4 .m4v .mov .avi .mkv .wmv .webm .mpeg .mpg .3gp .3g2 .ts .m2ts .mts
```

注意：

- `ImageCatalog.IsSupportedMediaPath` 决定目录里是否出现该文件。
- `ImageCatalog.IsSupportedStillImagePath` 只看扩展名，不代表一定能成功解码。
- `ImageCatalog.IsLikelyAnimatedImagePath` 当前只把 `.gif` 和 `.apng` 当作轻量统计里的动图。
- 新增格式时更新 `MediaFormatRegistry`，文件关联会自动同步静态图片扩展名。

## 图片加载

`ImageLoader` 是图片解码中心。

核心策略：

- 常规加载先尝试 WIC。
- WIC 失败后，在可兜底的异常类型上使用 Magick.NET。
- Radiance HDR 有单独路径。
- WIC 预览加载先用 `BitmapCacheOption.None` 读取尺寸，再请求目标解码尺寸。WPF 的 JPEG 和 PNG 解码器能原生按指定尺寸解码；其他 WIC 解码器可能先解码原始尺寸，因此超过完整解码阈值时不会进入该路径。
- 超过完整解码阈值的 JPEG/PNG 会标记为 `IsLargeImagePreview`：普通性能档最长边 `4096`，高性能档最长边 `8192`。该文档不会进入完整图缓存、空闲升级或邻图预加载，缩放不超过预览像素的 1:1。
- 裁剪、压缩和设置壁纸等原始像素操作会要求完整文档，因此会对超大图安全预览给出提示并停止；显示、切图、缩略图和文件信息仍可使用。
- Magick.NET 路径先用 `MagickImageInfo` 探测尺寸；普通超大格式不创建 `MagickImage`，因为当前依赖没有实现可靠的通用分块缩放解码。BMP、WebP、HEIF/AVIF 等非 JPEG/PNG 超大格式会安全拒绝。
- TIFF/BigTIFF 是受控例外。`IsTiffContainer` 读取 4 字节文件签名识别普通 TIFF 和 BigTIFF，不依赖扩展名；`MagickImageInfo.ReadCollection` 只枚举 IFD 元数据，`TiffOverviewSelector` 排除主层、异常宽高比和超预算层，再通过 `MagickReadSettings.FrameIndex/FrameCount` 只读取选中的缩略图或金字塔层。没有安全候选层时不会尝试主层。
- BigTIFF 标准档候选层解码预算约为目标最长边平方的两倍，高性能档最高仍受 `120_000_000` 像素限制；目标很小的缩略图至少允许读取 `4_000_000` 像素以内的内置预览层。选中层解码后还会再次核对实际尺寸。
- 完整加载会读取动画帧、Exif 方向、格式名、文件大小和时间。
- WPF `BitmapSource` 在线程间使用前应尽量 `Freeze()`。

保护阈值：

- 普通图片最大像素数：`120_000_000`。
- JPEG/PNG 直接安全预览源图最大像素数：`2_000_000_000`；带安全金字塔层的 BigTIFF 不使用主层像素数作为预览解码预算。
- 超大图安全预览最长边：普通档 `4_096`，高性能档 `8_192`；显示预览缓存预算达到 `1_024 MB` 时使用高性能档。
- HDR 最大像素数：`40_000_000`。
- 动画最大帧数：`2_000`。
- 动画最大像素帧：`300_000_000`。
- 动画最小帧延迟会被夹到可用值，避免极小 delay 导致播放异常。

新增解码逻辑时，优先给 `tests\Pixora.SmokeTests` 加样本或生成样本，至少覆盖“能解码”和“失败不会逃逸到 UI 崩溃”两个方向。

## 视频封面

`VideoThumbnailLoader` 不播放视频，只生成封面文档：

- 通过 Windows Shell 的 `IShellItemImageFactory` 读取系统缩略图。
- 如果系统不给缩略图，生成内置 fallback 封面。
- 主文档格式名为“视频 / 封面预览”。
- 视频文件在目录里显示、可收藏、可另存封面，但不参与裁剪、压缩、壁纸等静态图片操作。

## 渲染控件

`Controls\BitmapViewer.cs` 是主图渲染控件，用于按 `ViewScale`、`OffsetX`、`OffsetY` 绘制当前 `BitmapSource`。它避免依赖普通 `Image` 控件的一些布局行为，便于控制缩放、平移和渲染测试。

相关数学在 `ImageViewportMath`：

- 适应窗口计算。
- 不默认放大小图。
- 裁剪选择框到图片像素坐标的转换。

测试里有对高图完整渲染、适应窗口和裁剪数学的回归覆盖。

## 缓存

当前有四类缓存：

- `ImageCache`：完整静态图片文档内存缓存，按估算解码字节数做 LRU，动图不会进入该缓存。
- `BitmapSourceMemoryCache`：显示预览内存缓存，带文件长度和修改时间校验，避免文件变化后使用旧预览。
- `MainWindow` 内的 `_thumbnailCache`：缩略图内存缓存，最大条目数由 `MaxThumbnailCacheItems` 控制。
- `ThumbnailImageLoader`：缩略图解码协调，优先走 WIC，必要时回退受尺寸限制的显示预览或视频封面，并对接磁盘缓存；静态图缩略图失败时不会再直接触发完整解码。
- `ThumbnailDiskCache`：可选缩略图磁盘缓存，位于 `%LOCALAPPDATA%\Pixora\thumbnail-cache`，按最近访问时间自动淘汰；统计、容量整理和清空均提供可取消的异步入口，设置窗口不会在 UI 线程枚举大量缓存文件。

缩略图数据源由 `LazyIndexedList` 和 `VirtualizedRowCollection` 组成：WPF 看到完整行数，但只在容器真正访问某一行时创建该行及其一到两个 `ThumbnailItem`，不再因十几万文件一次性分配全部行和项；运行期最多保留最近 2,048 个项模型和 512 个行模型，选中项与正在加载项不会被淘汰。列表容器使用 Recycling 虚拟化。缩略图加载同时使用“目录代次”和“视口代次”校验；滚动开始时会取消上一视口仍在排队或解码的任务，滚动过程中只维护当前可见范围而不继续扩张解码队列，停稳约 180 毫秒后再加载可见区域及缓冲行。旧任务即使稍后返回，也不能覆盖新视口状态或移除新队列项。

设置里可以调整：

- 主图缓存大小。
- 显示预览缓存大小。
- 是否根据系统可用内存自动缩小两级内存缓存；手动容量始终作为上限。
- 低内存保护。
- 是否启用缩略图磁盘缓存和其容量上限。

`MemoryCacheCoordinator` 统一主图与显示预览缓存的运行期预算和硬件性能档位。设置页用明确的“自动（推荐）/手动”模式选择代替隐藏的复选框语义：自动模式忽略手动下拉值，使用专用的 `8 GB / 2 GB` 最高档再按运行环境缩放，并现场显示实际预算；16 GB 档约 `2 GB / 512 MB`，32 GB 档约 `4 GB / 1 GB`，64 GB 及以上约 `8 GB / 2 GB`。手动模式才读取并启用两个容量下拉框。系统内存负载达到 GC 高内存阈值的 85%，或 Pixora 进程工作集达到至少 512 MB 且超过可用内存约四分之一时，会提前停止后台预加载、收缩两级缓存，并只保留当前活跃范围的缩略图内存缓存。

## 设置持久化

`ViewerSettings` 写入：

```text
%LOCALAPPDATA%\Pixora\viewer-settings.json
```

`ViewerSettings`、`ShortcutSettings`、`FavoriteStore` 和 `BatchCompressionSettings` 统一通过 `AtomicJsonFile` 保存：先在同目录写入并刷盘临时文件，再原子替换目标文件，同时保留 `.bak`。主文件损坏或无法读取时会尝试备份；保存只发生在设置或用户数据变化时，不进入浏览图片和缩略图加载热路径。

当前包含：

- 缩略图栏显示/隐藏。
- 缩略图单列/双列。
- 快速搜索默认使用序号还是文件名。
- 是否在启动并完成初始目录加载后自动显示快速搜索；手动打开搜索会自动开启该偏好，设置页可关闭。
- 跳转成功后是否自动收起快速搜索；默认关闭，便于连续查找。
- 快速搜索通过 Ctrl+拖动设置的位置偏移；退出程序后保留，通过快速搜索快捷键关闭并重新打开时恢复默认位置。
- 保存后的打开行为。
- 删除确认。
- 排序方式。
- 上次打开目录。
- 启动时是否自动打开上次目录。
- 动图控制显示。
- 操作通知显示。
- 是否显示缩放比例，以及使用绝对百分比还是相对“适应窗口”的倍数。
- 空闲时是否加载完整分辨率。
- 主图缓存和显示预览缓存大小。
- 是否根据可用内存自动调整内存缓存。
- 低内存保护。
- 缩略图磁盘缓存和容量上限。
- 快捷键设置窗口大小、位置和最大化状态。
- 主窗口大小、位置和最大化状态。
- 是否始终最大化启动、是否复用已有窗口。
- 是否在切图时保持缩放与位置。
- 是否自动监视当前目录变化。
- 诊断信息是否允许包含完整本机路径。

`ShortcutSettings` 写入：

```text
%LOCALAPPDATA%\Pixora\shortcuts.json
```

`FavoriteStore` 写入：

```text
%LOCALAPPDATA%\Pixora\favorites.json
```

`BatchCompressionSettings` 写入：

```text
%LOCALAPPDATA%\Pixora\batch-compression-settings.json
```

它同时保存批量压缩参数、上次输入/输出位置，以及批量压缩窗口的大小、位置和最大化状态。

新增设置时要考虑三个问题：

- 新用户默认值。
- 旧 JSON 缺少字段时的默认值。
- 设置窗口保存时是否会覆盖运行期已经变化的状态。

## 快捷键系统

快捷键动作定义在：

```text
src\Pixora\Models\ShortcutAction.cs
```

动作名称、分组、上下文和默认排序在：

```text
src\Pixora\Services\ShortcutSettings.cs
src\Pixora\ShortcutSettingsWindow.xaml.cs
```

关键概念：

- `ShortcutAction` 是稳定动作标识。
- `ShortcutSettings.ActionInfos` 定义显示名、分组和上下文。
- `ResetToDefaults` 定义默认键位。
- `KeyboardShortcut` 封装键和修饰键。
- 设置窗口顶部搜索框始终可见；普通设置按分区过滤，快捷键按分类、动作名称和按键文本过滤。快捷键页同时支持编辑、冲突提示、恢复默认和持久化。
- 主窗口负责把实际按键转换为动作执行。
- `QuickSearchInteractionState` 统一判断快速搜索处于“快捷键状态”还是“文字输入状态”：快捷键状态隐藏光标并优先分发查看器快捷键，未绑定字符保持无动作；只有明确点击输入框后才显示系统闪烁光标，文字和常用编辑键优先。打开浮层和点击输入框外都会把键盘焦点交还主窗口，避免搜索框仅因可见就接收输入。
- `Ctrl+K` 默认打开快速搜索；序号解析和文件名压缩显示由 `QuickSearchMatcher` 提供可测试逻辑。
- `QuickSearchIndex` 不在目录加载时预先提取全部文件名；只有首次实际进行名称搜索时才在线程池初始化当前目录的按需条目，并支持取消。连续输入且新关键词延续旧关键词时，只继续筛选上一轮候选集。

新增快捷键时必须同时更新动作枚举、显示信息、默认键位、执行分发和测试。

## 收藏

收藏由 `FavoriteStore` 管理，内部用不区分大小写的全路径集合。

行为要点：

- 收藏可以保存图片和视频路径。
- 收藏视图通过 `ImageCatalog.LoadFromPaths` 构建，不绑定某个来源目录。
- 加载收藏视图时会剔除不存在或不支持的路径。
- UI 中缩略图项有 `IsFavorite`，当前图片收藏变化后要同步缩略图状态。

如果修改收藏逻辑，重点检查收藏视图下的删除、排序、切图、缩略图状态和退出收藏视图后的行为。

## 裁剪和圆形裁剪

裁剪状态主要在 `MainWindow` 内：

- `_isCropMode`
- `_cropShape`
- `_cropSelectionRect`
- `_cropDragMode`

矩形选择框由屏幕坐标转换成图片像素坐标时使用 `ImageViewportMath.CalculateCropPixelRect`。圆形裁剪会生成带 alpha 的方形 PNG。裁剪最大像素数由 `MaxCropPixelCount` 保护。

后续修改裁剪交互时，注意：

- 选择框不能跑出图片有效区域。
- 缩放、平移和旋转状态会影响用户看到的位置。
- 保存裁剪是异步写文件，完成后根据设置决定是否打开新文件。

## 单图压缩

单图压缩窗口是：

```text
CompressImageWindow.xaml
CompressImageWindow.xaml.cs
```

核心服务是：

```text
Services\ImageCompressor.cs
```

支持 JPEG 和 PNG。JPEG 有质量参数，PNG 走压缩策略。压缩可以选择最大宽高，默认不放大小图。输出默认文件名会在原文件名后追加中文后缀。

## 批量压缩

批量压缩窗口是：

```text
BatchCompressWindow.xaml
BatchCompressWindow.xaml.cs
```

核心服务是：

```text
Services\BatchImageCompressor.cs
Services\BatchCompressionSettings.cs
```

流程：

1. 用户选择输入文件或目录。
2. 选择输出目录、格式、质量、最大宽高、是否包含子目录、是否覆盖。
3. 预扫描统计可压缩、动图跳过、读取失败。
4. 用户确认后执行压缩。
5. 输出目录写入批量压缩日志。

`ProgressUpdateBuffer` 接收后台线程的全部进度消息，窗口约每 75 毫秒批量取出一次：进度条和状态文本只使用最新值，日志仍按原顺序完整追加，但每批只滚动一次列表，从而降低大批量任务的 UI 调度开销。

限制：

- 当前只处理静态图片。
- GIF/APNG/WebP 等可能动图的内容在预扫描中会被保护性跳过或失败。
- 输出目录在输入目录内时会提示风险。

## 批量删除

批量删除入口在主窗口右键菜单。它针对当前目录媒体文件，支持预览候选项并删除到回收站。

关键点：

- 使用 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` 进入回收站。
- 收藏视图下禁用“批量删除当前目录媒体”，因为收藏视图不是一个真实单目录。
- 删除后会重载当前目录并刷新当前图片。

## 设置桌面壁纸

主窗口的“设为桌面壁纸”只对静态图片显示，不对视频和动图显示。

实现流程：

1. 确认当前文档是静态图片。
2. 按当前显示级旋转生成壁纸位图。
3. 写入 `%LOCALAPPDATA%\Pixora` 下的 `Pixora-wallpaper-*.jpg`。
4. 调用 `SystemParametersInfo(SPI_SETDESKWALLPAPER)`。
5. 清理旧的 Pixora 壁纸临时文件。

维护时要保留“动图不显示该功能”的规则，避免把动画帧错误保存成壁纸。

## 文件关联和默认应用

`FileAssociationService` 负责 Windows 文件关联。

普通注册做的事：

- 写入 `HKCU\Software\Classes\Pixora.Image`。
- 写入打开命令：`"Pixora.exe" "%1"`。
- 写入 `Applications\Pixora.exe` 打开命令。
- 写入 `Software\Pixora\Capabilities\FileAssociations`。
- 写入 `HKCU\Software\RegisteredApplications`。
- 把 `Pixora.Image` 加到各扩展名的 `OpenWithProgids`。
- 调用 `SHChangeNotify` 通知 Shell 更新关联。
- 启动时只在已有 `Pixora.Image` 打开命令指向已不存在的程序文件时重写为当前 `Pixora.exe`；仍然有效的安装副本不会被其他副本覆盖，也不会创建新关联或修改 Windows 默认应用选择。

默认应用限制：

- Windows 10/11 通常不允许普通桌面应用静默把自己设为默认应用。
- `TrySetDefaultAssociationsSilently` 会先检测旧 COM 路径是否可用；现代系统大多会失败。
- 官方 fallback 是打开 `ms-settings:defaultapps?registeredAppUser=Pixora` 让用户确认。
- “高级一键默认”依赖用户自行提供的 `SetUserFTA.exe`。这是非官方高级方案，不能内置到开源仓库。

## 本地窗口和 UI 状态

当前已经持久化的 UI 状态包括：

- 设置窗口大小、位置、最大化状态。
- 缩略图栏显示状态。
- 缩略图栏单列/双列。
- 排序方式。
- 上次目录和是否启动自动打开。
- 动图控制和操作通知等显示设置。

主窗口尺寸、位置和最大化状态保存在 `ViewerSettings`。恢复前会验证保存区域仍与当前虚拟桌面相交，避免更换显示器、DPI 或分辨率后窗口完全落在屏幕外。

主窗口关闭 IME 组合以保证字母快捷键稳定；快速搜索输入框会局部重新启用 IME，因此按文件名查找时仍可输入中文。设置窗口等需要文字输入的界面也保留正常输入法行为。

## 测试覆盖

运行：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj
```

当前 smoke test 覆盖：

- 自然排序和多种排序模式。
- 打开单文件占位与完整目录替换。
- 目录后台加载取消。
- 常见图片、GIF、HDR、AVIF 解码。
- 大帧数 GIF 的安全加载。
- 视频路径识别和视频封面 fallback。
- 图片缓存和预览缓存行为。
- 缩略图磁盘缓存读取、容量清理和手动清理。
- `ThumbnailImageLoader` 的尺寸约束与磁盘缓存复用。
- 媒体格式注册与文件关联格式一致性。
- 内存缓存协调和滚动日志文件。
- 收藏持久化。
- 快捷键默认值和保存。
- 设置持久化。
- 适应窗口和裁剪数学。
- 主窗口输入法隔离、大目录工具控件初始化和设置字段持久化。
- 缩放提示百分比/相对倍数格式和对应设置持久化。
- 超大图策略、BigTIFF 签名识别、金字塔候选层选择和安全预览边界。
- 目录索引移除路径和扫描进度报告。
- 十五万路径快速搜索索引、连续关键词候选复用和取消。
- 十五万项缩略图按需行/项实例化，确保只创建被访问的少量模型。
- 文件排序元数据快照复用及目录变化后的精确失效。
- 十万次批量进度上报的顺序、排空和耗时基准。
- 目录新增、修改、删除、重命名的增量索引更新与监听批处理。
- 缩略图磁盘缓存异步统计、清理和取消。
- 快速滚动时的缩略图视口取消、代次隔离和停稳恢复。
- Per-Monitor V2 manifest、快速搜索键盘导航和关键动态状态无障碍属性。
- 快速搜索输入状态、快捷键状态和光标显示规则。
- JSON 原子替换、备份回退和临时文件清理。
- 圆形裁剪 alpha。
- JPEG/PNG 单图压缩。
- 批量压缩预扫描、跳过动图、失败记录和输出。
- `BitmapViewer` 高图渲染不裁切。
- 外部真实样本目录压力测试入口。

带外部目录：

```powershell
dotnet run --project tests\Pixora.SmokeTests\Pixora.SmokeTests.csproj -- --media-folder "D:\Samples" --sample-count 50 --video-sample-count 5
```

还可以直接传入单张外部图片路径，测试预览、完整加载和渲染是否完整。

## 依赖

主要依赖：

- WPF / Windows Desktop SDK。
- `Magick.NET-Q16-AnyCPU`：WIC 不支持或失败时的图片解码和压缩能力。
- Windows Shell COM：视频缩略图、文件关联通知等系统集成。
- `Microsoft.VisualBasic.FileIO`：删除到回收站。

发布目标：

- `net9.0-windows`
- 默认 runtime：`win-x64`
- 默认非 self-contained，需要用户机器有对应 .NET 运行环境。

## 已知技术债

- `MainWindow.xaml.cs` 仍较大，但目录后台加载、目录变更监听、快速搜索索引、媒体格式判断和缓存预算已提取到独立服务；后续可继续拆分缩略图、编辑和文件操作的 UI 协调代码。
- 默认应用自动设置受 Windows 保护限制，不能保证静默成功。
- 旧名称 `Pic-O` 和 `PureView` 仍需要在数据迁移逻辑中保留，避免老用户设置丢失。
- 当前测试偏服务和渲染 smoke test，缺少完整 UI 自动化。
- 目录索引只读当前目录；如果未来要支持递归浏览，需要重新设计性能、排序、删除和缩略图策略。
