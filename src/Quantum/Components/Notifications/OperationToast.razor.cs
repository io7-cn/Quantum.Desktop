using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Quantum.Localization;

namespace Quantum.Components;

public partial class OperationToast : IAsyncDisposable
{
    private readonly string _registrationId = Guid.NewGuid().ToString("N");
    private DotNetObjectReference<OperationToast>? _reference;
    private OperationNotice? _renderedNotice;
    private ElementReference _element;
    private bool _disposed;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = null!;

    [Inject]
    private IStringLocalizer<AppStrings> L { get; set; } = null!;

    [Parameter]
    public OperationNotice? Notice { get; set; }

    [Parameter]
    public EventCallback<OperationNotice?> NoticeChanged { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || ReferenceEquals(_renderedNotice, Notice))
        {
            return;
        }

        _renderedNotice = Notice;
        if (Notice is { } notice)
        {
            _reference ??= DotNetObjectReference.Create(this);
            await JavaScript.InvokeVoidAsync("quantum.operationToast.show",
                _registrationId, _element, _reference, notice.Id, notice.IsError);
        }
        else
        {
            await JavaScript.InvokeVoidAsync("quantum.operationToast.dispose", _registrationId);
        }
    }

    [JSInvokable]
    public Task DismissAsync(string noticeId)
    {
        return InvokeAsync(() =>
        {
            return !_disposed && Notice?.Id == noticeId
                ? NoticeChanged.InvokeAsync(null) : Task.CompletedTask;
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_reference is not null)
            {
                await JavaScript.InvokeVoidAsync("quantum.operationToast.dispose", _registrationId);
            }
        }
        catch (JSDisconnectedException)
        {
            // The WebView already released its timers and event listeners.
        }
        finally
        {
            _reference?.Dispose();
        }
    }
}
