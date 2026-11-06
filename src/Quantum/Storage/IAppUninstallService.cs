namespace Quantum.Storage;

internal interface IAppUninstallService
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    Task RequestAsync();
}
