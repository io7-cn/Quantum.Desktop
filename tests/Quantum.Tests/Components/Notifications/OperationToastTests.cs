using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Quantum.Components;

namespace Quantum.Tests;

public sealed class OperationToastTests
{
    [Fact]
    public async Task CallbackFromAnOlderIdenticalMessageCannotDismissItsReplacement()
    {
        var first = new OperationNotice("Installed plugin");
        var replacement = new OperationNotice("Installed plugin");
        var dismissals = 0;
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton<IComponentActivator>(activator).AddSingleton<IJSRuntime, NoOpJavaScript>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var callback = EventCallback.Factory.Create<OperationNotice?>(this, _ => dismissals++);
            var root = await renderer.RenderComponentAsync<OperationToast>(Parameters(first, callback));
            await activator.Toast!.SetParametersAsync(Parameters(replacement, callback));

            await activator.Toast.DismissAsync(first.Id);
            Assert.Equal(0, dismissals);
            Assert.Contains("Installed plugin", root.ToHtmlString());

            await activator.Toast.DismissAsync(replacement.Id);
            Assert.Equal(1, dismissals);
            await activator.Toast.DisposeAsync();
            await activator.Toast.DismissAsync(replacement.Id);
            Assert.Equal(1, dismissals);
        });
    }

    private static ParameterView Parameters(OperationNotice notice, EventCallback<OperationNotice?> callback)
        => ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(OperationToast.Notice)] = notice,
            [nameof(OperationToast.NoticeChanged)] = callback
        });

    private sealed class CapturingActivator : IComponentActivator
    {
        public OperationToast? Toast { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is OperationToast toast)
            {
                Toast = toast;
            }

            return component;
        }
    }

    private sealed class NoOpJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
            => ValueTask.FromResult(default(T)!);
    }
}
