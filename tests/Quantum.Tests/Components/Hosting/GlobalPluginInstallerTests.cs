using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Quantum.Components;
using Quantum.Plugins;
using System.Reflection;

namespace Quantum.Tests;

public sealed class GlobalPluginInstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingInstallerReleasesPreviewEvenWhenScanFinishesLater(bool finishAfterDispose)
    {
        IPluginRuntimeManager runtime = DispatchProxy.Create<IPluginRuntimeManager, RuntimeStub>();
        var stub = (RuntimeStub)runtime;
        var installer = new GlobalPluginInstaller
        {
            PluginRuntimeManager = runtime,
            JSRuntime = new JavaScriptStub()
        };
        var preview = new PluginInstallPreview("preview", "example.zip", [], []);
        Task preparing = installer.PrepareInstallAsync(new InputFileChangeEventArgs([new ArchiveFile()]));

        if (!finishAfterDispose)
        {
            stub.Prepared.SetResult(preview);
            await preparing;
        }

        await installer.DisposeAsync();
        if (finishAfterDispose)
        {
            Assert.True(stub.PrepareToken.IsCancellationRequested);
            stub.Prepared.SetResult(preview);
            await preparing;
        }

        Assert.Equal(["preview"], stub.CancelledPreviewIds);
    }

    public class RuntimeStub : DispatchProxy
    {
        public TaskCompletionSource<PluginInstallPreview> Prepared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> CancelledPreviewIds { get; } = [];

        public CancellationToken PrepareToken { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IPluginRuntimeManager.PrepareInstallAsync):
                    PrepareToken = (CancellationToken)args![2]!;
                    return Prepared.Task;
                case nameof(IPluginRuntimeManager.CancelInstallAsync):
                    CancelledPreviewIds.Add((string)args![0]!);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private sealed class ArchiveFile : IBrowserFile
    {
        public string Name => "example.zip";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => 0;
        public string ContentType => "application/zip";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            return new MemoryStream();
        }
    }

    private sealed class JavaScriptStub : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
