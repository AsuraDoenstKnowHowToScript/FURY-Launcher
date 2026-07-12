// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App;

public partial class App : Application
{
    /// <summary>App-wide service container (Phase 1 foundation for MVVM in Phase 2).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LauncherCore>();            // Core facade (resolved lazily)
        services.AddSingleton<SelectedInstanceService>(); // shared "selected instance" state
        services.AddTransient<MainWindowViewModel>();
        Services = services.BuildServiceProvider();

        // Resolve views from view models by name convention.
        DataTemplates.Add(new ViewLocator());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window's own code-behind still drives the current tabs (Phase 1);
            // the view model is attached now so Phase 2 can migrate screen by screen.
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
