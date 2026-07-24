// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Net;
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
    public LogHub Logs { get; }
    public GameLauncher Game { get; }
    public ModService Mods { get; }
    public ModMetadataService ModMetadata { get; }
    public PackService Packs { get; }
    public MrpackService Mrpacks { get; }
    public ProfileService Profiles { get; }
    public AccountService Accounts { get; }
    public MojangSkinService MsSkins { get; }
    public SkinApplyService Skins { get; }
    public JavaInstaller Java { get; }
    public SettingsService Settings { get; }
    public VersionListService Versions { get; }
    public UpdateService Updates { get; }

    /// <param name="root">Data root; defaults to <c>%APPDATA%/FURY Launcher</c>.</param>
    public LauncherCore(string? root = null)
    {
        Paths = new LauncherPaths(root);
        // Tuned for low-latency Modrinth calls: request gzip/brotli so the search
        // JSON arrives compressed, keep pooled connections warm, and prefer HTTP/2
        // so search + icon requests multiplex over one connection instead of each
        // paying a fresh TLS handshake.
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        Instances = new InstanceService(Paths);
        Auth = new AuthManager(Paths);
        Logs = new LogHub();
        Game = new GameLauncher(Paths, new LoaderInstaller(_http), Instances, Logs);
        var modrinth = new ModrinthClient(_http);
        ModMetadata = new ModMetadataService(Paths, modrinth);
        // CurseForge key: env var, then embedded (CI), then a per-machine file. Empty = source off.
        var curseforge = new CurseForgeClient(_http, CurseForgeKey.Resolve(Paths.CurseForgeKeyFile));
        Mods = new ModService(Paths, modrinth, ModMetadata, curseforge);
        Packs = new PackService(Paths, Instances);
        Mrpacks = new MrpackService(_http, Paths, Instances);
        Profiles = new ProfileService(Paths);
        Skins = new SkinApplyService(Paths, Mods);
        Java = new JavaInstaller(_http, Paths);
        JavaLocator.ManagedRoot = Paths.JavaDir; // so scans find launcher-installed JREs
        Settings = new SettingsService(Paths);
        // Unified accounts (offline + Microsoft) with a transparent one-time migration from
        // profiles.json, plus read-only Microsoft skin fetch. Both are UI-agnostic.
        Accounts = new AccountService(Paths, Settings, Auth);
        MsSkins = new MojangSkinService(_http, Paths);
        Versions = new VersionListService(_http);
        Updates = new UpdateService(_http);
    }

    public void Dispose() => _http.Dispose();
}
