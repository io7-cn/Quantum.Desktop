using Quantum.Storage;

namespace Quantum.WinUI;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopAppStorage.ConfigureProcess();
        // Only production builds may inspect or migrate the production installation.
        if (!DesktopBuildProfile.Current.IsPreview)
        {
            Velopack.VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnAfterUpdateFastCallback(_ => WindowsDesktopStorage.RegisterUninstaller())
                .Run();
            if (!WindowsDesktopStorage.Initialize())
            {
                Environment.ExitCode = 1;
                return;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
