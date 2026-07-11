// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Services;

namespace Launcher.Core;

/// <summary>
/// Single entry point the UI news up. Owns and wires all services so the app
/// only depends on this one type. 100% usable without any UI.
/// </summary>
public sealed class LauncherCore : IDisposable
{
    private readonly HttpClient _http;

    public LauncherPaths Paths { get; }
    public InstanceService Instances { get; }
    public AuthManager Auth { get; }
    public GameLauncher Game { get; }
    public ModService Mods { get; }
    public PackService Packs { get; }
    public ProfileService Profiles { get; }
    public SkinApplyService Skins { get; }
    public SettingsService Settings { get; }
    public VersionListService Versions { get; }
    public UpdateService Updates { get; }

    /// <param name="root">Data root; defaults to <c>%APPDATA%/FURY Launcher</c>.</param>
    public LauncherCore(string? root = null)
    {
        Paths = new LauncherPaths(root);
        _http = new HttpClient();

        Instances = new InstanceService(Paths);
        Auth = new AuthManager(Paths);
        Game = new GameLauncher(Paths, new LoaderInstaller(_http), Instances);
        Mods = new ModService(Paths, new ModrinthClient(_http));
        Packs = new PackService(Paths, Instances);
        Profiles = new ProfileService(Paths);
        Skins = new SkinApplyService(Paths, Mods);
        Settings = new SettingsService(Paths);
        Versions = new VersionListService(_http);
        Updates = new UpdateService(_http);
    }

    public void Dispose() => _http.Dispose();
}
