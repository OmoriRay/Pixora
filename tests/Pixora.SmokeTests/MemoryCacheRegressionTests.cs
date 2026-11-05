using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pixora.Models;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class MemoryCacheRegressionTests
{
    public static void Run()
    {
        AssertImageCache();
        var folder = Path.Combine(Path.GetTempPath(), "Pixora-memory-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "sample.bin");
            File.WriteAllBytes(path, [1]);
            AssertBitmapCache(path);
            RunHitBenchmark(path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void AssertImageCache()
    {
        var bitmap = CreateBitmap(1);
        ImageDocument Document(string key, BitmapSource? source = null) =>
            new(key, source ?? bitmap, "PNG", 1, DateTime.UnixEpoch);
        var cache = new ImageCache(12);
        cache.Add(Document("A"));
        cache.Add(Document("B"));
        cache.Add(Document("C"));
        Require(cache.TryGet("a", out _), "主图缓存命中应忽略大小写。");
        cache.Add(Document("D")); // D, A, C；B 最旧。
        Require(!cache.TryGet("B", out _), "命中必须更新主图 LRU 顺序。");
        var replacement = Document("a", CreateBitmap(2));
        cache.Add(replacement); // A=8, D=4；C 淘汰。
        Require(cache.Count == 2 && cache.TotalEstimatedBytes == 12, "替换必须更新主图字节数且只保留一个节点。");
        Require(!cache.TryGet("C", out _) && cache.TryGet("A", out var actual)
            && ReferenceEquals(actual, replacement), "大小写替换必须返回新文档并正确淘汰最旧项。");
        cache.Remove("a");
        cache.Remove("missing");
        Require(cache.Count == 1 && cache.TotalEstimatedBytes == 4 && !cache.TryGet("A", out _),
            "主图 Remove 必须同步字节数，重复/不存在删除应无害。");
        cache.Add(Document("E"));
        cache.Add(Document("F"));
        cache.TrimToBytes(4);
        Require(cache.MaxBytes == 12 && cache.Count == 1 && cache.TryGet("F", out _),
            "临时预算收缩应保留最新主图且不修改配置预算。");
        cache.Add(Document("G"));
        cache.SetMaxBytes(0);
        Require(cache.MaxBytes == 1 && cache.Count == 1 && cache.TryGet("G", out _),
            "主图永久预算最小为 1，超预算时仍保留最新一条。");
        cache.Remove("G");
        cache.Add(Document("H"));
        Require(cache.Count == 1 && cache.TotalEstimatedBytes == 4 && cache.TryGet("H", out _),
            "清空后应能重新加入主图。");
        var animated = new ImageDocument("animated", bitmap, "GIF", 1, DateTime.UnixEpoch,
            [new(bitmap, TimeSpan.FromMilliseconds(100)), new(bitmap, TimeSpan.FromMilliseconds(100))]);
        cache.Add(animated);
        Require(cache.Count == 1 && !cache.TryGet("animated", out _), "LRU 改动必须保留动图不缓存行为。");
    }

    private static void AssertBitmapCache(string path)
    {
        var bitmap = CreateBitmap(1);
        var cache = new BitmapSourceMemoryCache(12);
        string Key(string variant) => path + "|" + variant;
        void Add(string key, BitmapSource? source = null) => cache.Add(Key(key), path, source ?? bitmap);
        bool Has(string key) => cache.TryGet(Key(key), path, out _);
        Add("A");
        Add("B");
        Add("C");
        Require(Has("a"), "预览命中应忽略大小写。");
        Add("D");
        Require(!Has("B"), "预览命中必须更新淘汰顺序。");
        var replacement = CreateBitmap(2);
        Add("a", replacement);
        Require(cache.TotalEstimatedBytes == 12 && !Has("C")
            && cache.TryGet(Key("A"), path, out var actual) && ReferenceEquals(actual, replacement),
            "预览替换应更新字节数、返回新位图并淘汰最旧项。");
        cache.RemoveByPath(path.ToUpperInvariant());
        cache.RemoveByPath(path);
        Require(cache.TotalEstimatedBytes == 0 && !Has("A") && !Has("D"),
            "RemoveByPath 应忽略大小写并删除所有预览变体和节点。");
        Add("E");
        Add("F");
        Add("G");
        cache.TrimToBytes(4);
        Require(cache.MaxBytes == 12 && cache.TotalEstimatedBytes == 4 && Has("G") && !Has("F") && !Has("E"),
            "预览临时预算应保留最新项且不修改配置。");
        Add("H");
        cache.SetMaxBytes(0);
        Require(cache.MaxBytes == 1 && cache.TotalEstimatedBytes == 4 && Has("H") && !Has("G"),
            "预览永久预算最小为 1，仍保留超预算的最新一条。");
        File.AppendAllText(path, "changed");
        Require(!Has("H") && cache.TotalEstimatedBytes == 0, "文件变化应删除失效预览及其节点。");
        Add("I");
        Require(Has("I") && cache.TotalEstimatedBytes == 4, "失效清空后应能重新加入预览。");
    }

    private static BitmapSource CreateBitmap(int width)
    {
        var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, new byte[width * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // 固定种子、预生成键、预热、三轮中位数；仅输出耗时，不以机器速度决定测试成败。
    private static void RunHitBenchmark(string path)
    {
        var bitmap = CreateBitmap(1);
        foreach (var count in new[] { 256, 4096 })
        {
            var keys = Enumerable.Range(0, count).Select(index => "cache-key-" + index).ToArray();
            var random = new Random(1729);
            var accesses = Enumerable.Range(0, 8192).Select(_ => keys[random.Next(count)]).ToArray();
            var image = new ImageCache(count * 4L);
            var preview = new BitmapSourceMemoryCache(count * 4L);
            var oldImage = new TraversingHitBaseline(path, checkStamp: false);
            var oldPreview = new TraversingHitBaseline(path, checkStamp: true);
            foreach (var key in keys)
            {
                var document = new ImageDocument(key, bitmap, "PNG", 1, DateTime.UnixEpoch);
                image.Add(document);
                preview.Add(key, path, bitmap);
                oldImage.Add(key, document);
                oldPreview.Add(key, document);
            }

            bool ImageHit(string key) => image.TryGet(key, out _);
            bool PreviewHit(string key) => preview.TryGet(key, path, out _);
            foreach (var key in accesses.Take(1024))
            {
                Require(ImageHit(key) && PreviewHit(key) && oldImage.TryGet(key) && oldPreview.TryGet(key),
                    "命中基准预热必须全部命中。");
            }

            var imageOld = new double[3];
            var imageNew = new double[3];
            var previewOld = new double[3];
            var previewNew = new double[3];
            for (var round = 0; round < 3; round++)
            {
                // 交替先后次序以减少统一执行顺序带来的偏差。
                if (round % 2 == 0)
                {
                    imageOld[round] = Measure(oldImage.TryGet, accesses);
                    imageNew[round] = Measure(ImageHit, accesses);
                    previewOld[round] = Measure(oldPreview.TryGet, accesses);
                    previewNew[round] = Measure(PreviewHit, accesses);
                }
                else
                {
                    imageNew[round] = Measure(ImageHit, accesses);
                    imageOld[round] = Measure(oldImage.TryGet, accesses);
                    previewNew[round] = Measure(PreviewHit, accesses);
                    previewOld[round] = Measure(oldPreview.TryGet, accesses);
                }
            }

            Report("ImageCache", imageOld, imageNew);
            Report("BitmapSourceMemoryCache（含文件戳读取）", previewOld, previewNew);
            void Report(string name, double[] oldSamples, double[] newSamples)
            {
                Array.Sort(oldSamples);
                Array.Sort(newSamples);
                Console.WriteLine($"{name} 命中基准：{count} 条 / {accesses.Length} 次，旧遍历 {oldSamples[1]:F2} ms，节点引用 {newSamples[1]:F2} ms，{oldSamples[1] / newSamples[1]:F2}x。");
            }
        }
    }

    private static double Measure(Func<string, bool> hit, string[] accesses)
    {
        var hits = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var key in accesses)
        {
            if (hit(key)) hits++;
        }
        stopwatch.Stop();
        Require(hits == accesses.Length, "命中基准不得混入未命中访问。");
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    // 复现改动前的命中热路径：同样的字典、锁、大小写比较、文件戳读取和逐节点查找。
    // 设置阶段不计时，使用同一张冻结位图，避免解码/位图内存影响 LRU 比较。
    private sealed class TraversingHitBaseline(string path, bool checkStamp)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ImageDocument> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _usage = new();
        private readonly long _length = new FileInfo(path).Length;
        private readonly long _ticks = new FileInfo(path).LastWriteTimeUtc.Ticks;

        public void Add(string key, ImageDocument document)
        {
            _items.Add(key, document);
            _usage.AddFirst(key);
        }

        public bool TryGet(string key)
        {
            var info = checkStamp ? new FileInfo(path) : null;
            var length = info?.Length;
            var ticks = info?.LastWriteTimeUtc.Ticks;
            lock (_gate)
            {
                if (!_items.TryGetValue(key, out _) || (checkStamp && (length != _length || ticks != _ticks)))
                    return false;
                var node = _usage.First;
                while (node is not null)
                {
                    if (string.Equals(node.Value, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _usage.Remove(node);
                        break;
                    }
                    node = node.Next;
                }
                _usage.AddFirst(key);
                return true;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
