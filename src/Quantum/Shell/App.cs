namespace Quantum;

public sealed class App(MainPage mainPage) : Microsoft.Maui.Controls.Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(mainPage)
        {
            Title = string.Empty,
            MinimumWidth = 960,
            MinimumHeight = 640,
            Width = 1280,
            Height = 820
        };

#if WINDOWS
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                nativeWindow.AppWindow.TitleBar.IconShowOptions =
                    Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu;
            }
        };
#endif

        return window;
    }
}
