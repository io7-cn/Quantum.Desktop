namespace Quantum.Updates;

/// <summary>Local previews never initialize the production installer or its update feed.</summary>
internal sealed class PreviewUpdateBackend : IAppUpdateBackend
{
    public bool IsInstalled => false;
    public string CurrentVersion => typeof(PreviewUpdateBackend).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string? PendingVersion => null;
    public Task<string?> CheckAsync() => Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress) => throw new InvalidOperationException("Preview builds update by rebuilding.");
    public Task ApplyAndRestartAsync() => throw new InvalidOperationException("Preview builds cannot apply production updates.");
}
