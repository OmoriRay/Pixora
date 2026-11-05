namespace Pixora.Services;

public interface IPortableMediaSource : IDisposable
{
    IReadOnlyList<string> Paths { get; }
    string InitialPath { get; }
    bool ContainsPath(string path);
    Task<string> EnsureLocalFileAsync(string path, CancellationToken token);
    Task<string> RetryLocalFileAsync(string path, CancellationToken token);
}
