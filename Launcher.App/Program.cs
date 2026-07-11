// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.IO;
using Avalonia;
using Launcher.Core.Services;

namespace Launcher.App;

internal static class Program
{
    // Avalonia entry point. Keep this minimal and free of app logic.
    [STAThread]
    public static void Main(string[] args)
    {
        // Route crash diagnostics to a crash.log next to the executable, and make sure
        // no exception dies silently (the app is self-contained; users have no console).
        CrashLog.Initialize(Path.Combine(AppContext.BaseDirectory, "crash.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("[fatal] unhandled exception: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("[fatal] unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("[fatal] startup crashed", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
