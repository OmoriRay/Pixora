using System.IO;
using System.Text;

namespace Pixora.Services;

public sealed class RollingTextFile
{
    public RollingTextFile(string path, long maximumBytes)
    {
        Path = path;
        MaximumBytes = Math.Max(1, maximumBytes);
    }

    public string Path { get; }

    public string PreviousPath => $"{Path}.previous";

    public long MaximumBytes { get; }

    public void Append(string content)
    {
        EnsureFolder();
        var incomingBytes = Encoding.UTF8.GetByteCount(content);
        if (File.Exists(Path)
            && new FileInfo(Path).Length > 0
            && new FileInfo(Path).Length + incomingBytes > MaximumBytes)
        {
            File.Move(Path, PreviousPath, overwrite: true);
        }

        File.AppendAllText(Path, content, Encoding.UTF8);
    }

    public void Clear()
    {
        EnsureFolder();
        File.WriteAllText(Path, string.Empty, Encoding.UTF8);
        File.Delete(PreviousPath);
    }

    public string ReadRecent(int maximumCharacters)
    {
        if (maximumCharacters <= 0 || !File.Exists(Path))
        {
            return string.Empty;
        }

        var content = File.ReadAllText(Path, Encoding.UTF8);
        return content.Length <= maximumCharacters
            ? content
            : content[^maximumCharacters..];
    }

    private void EnsureFolder()
    {
        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }
}
