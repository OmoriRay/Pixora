using System.IO;

namespace Pixora.Services;

/// <summary>Copies source bytes without re-encoding and publishes only a complete destination.</summary>
public static class OriginalFileCopy
{
    public static async Task SaveAsync(string sourcePath, string destinationPath, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("保存位置不能与设备临时副本相同，请选择电脑中的其他目录。");
        var portableRoot = Path.TrimEndingDirectorySeparator(
            Path.Combine(Path.GetTempPath(), "Pixora", "portable-media")) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(portableRoot, StringComparison.OrdinalIgnoreCase)
            || PortableMediaSource.IsShellTransferPath(destination))
            throw new IOException("请将副本保存到电脑中的普通目录，不要选择设备临时缓存目录。");

        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".pixora-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        // Deny writes and deletion until copying finishes, including session cleanup on navigation.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != input.Length)
                    throw new IOException("副本大小与原文件不一致，已停止保存。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
