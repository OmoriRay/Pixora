using System.IO;
using Pixora.Services;

namespace Pixora.SmokeTests;

internal static class OriginalFileCopyRegressionTests
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Pixora-copy-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.bin");
        var destination = Path.Combine(folder, "saved.bin");
        byte[] sourceBytes = [0, 255, 13, 10, 42, 128];
        byte[] previousBytes = [9, 8, 7];
        try
        {
            File.WriteAllBytes(source, sourceBytes);
            await OriginalFileCopy.SaveAsync(source, destination, overwrite: false);
            Require(File.ReadAllBytes(destination).SequenceEqual(sourceBytes), "导出必须逐字节保留原文件。");
            File.WriteAllBytes(destination, previousBytes);
            try
            {
                await OriginalFileCopy.SaveAsync(source, destination, overwrite: false);
                throw new InvalidOperationException("未允许覆盖时不能替换目标。");
            }
            catch (IOException) { }
            Require(File.ReadAllBytes(destination).SequenceEqual(previousBytes), "失败时原目标必须保持不变。");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    await OriginalFileCopy.SaveAsync(source, destination, true, cancellation.Token);
                    throw new InvalidOperationException("取消保存必须停止。");
                }
                catch (OperationCanceledException) { }
            }
            Require(File.ReadAllBytes(destination).SequenceEqual(previousBytes), "取消不能损坏已有目标。");
            using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    await OriginalFileCopy.SaveAsync(source, destination, overwrite: true);
                    throw new InvalidOperationException("目标被占用时替换必须失败。");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Require(File.ReadAllBytes(destination).SequenceEqual(previousBytes), "替换失败也必须保留原目标。");
            await OriginalFileCopy.SaveAsync(source, destination, overwrite: true);
            Require(File.ReadAllBytes(destination).SequenceEqual(sourceBytes), "明确允许覆盖时应保存完整新副本。");
            try
            {
                await OriginalFileCopy.SaveAsync(source, source, overwrite: true);
                throw new InvalidOperationException("不能覆盖源文件自身。");
            }
            catch (IOException) { }
            Require(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "任何保存操作都不能改动源文件。");
            Require(!Directory.EnumerateFiles(folder, ".pixora-copy-*.tmp").Any(), "成功、取消和失败后不应遗留导出暂存文件。");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine("原始副本保存回归通过。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
