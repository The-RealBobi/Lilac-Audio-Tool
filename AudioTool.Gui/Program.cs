using System;
using System.Globalization;
using Level5.AudioTool.Core.Cri;
using Avalonia;

namespace Level5.AudioTool.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cri")
        {
            try
            {
                string root = MainWindow.ResolveAudioRoot();
                string data = MainWindow.ResolveDataRoot();
                Console.WriteLine(CriCommands.Run(args[1..], root, data));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
