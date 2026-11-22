using UIKit;

namespace Quantum;

public static class Program
{
    public static void Main(string[] args)
    {
        DesktopAppStorage.ConfigureProcess();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
