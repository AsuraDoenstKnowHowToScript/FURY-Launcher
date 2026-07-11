// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Installs a mod loader for an instance and returns the resulting launch
/// version id (e.g. <c>fabric-loader-0.15.0-1.20.1</c> or
/// <c>1.20.1-forge-47.2.0</c>).
///
/// Fabric uses CmlLib's installer. Forge and NeoForge are installed via
/// <see cref="ForgeDirectInstaller"/>, which downloads the installer jar
/// directly from Maven (never the website / adfoc.us) and runs it headless.
/// </summary>
public sealed class LoaderInstaller
{
    private readonly HttpClient _http;
    private readonly ForgeDirectInstaller _forge;

    public LoaderInstaller(HttpClient http)
    {
        _http = http;
        _forge = new ForgeDirectInstaller(http);
    }

    public async Task<string> InstallAsync(
        Instance instance, MinecraftLauncher launcher, string minecraftDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        switch (instance.Loader)
        {
            case LoaderType.Fabric:
            {
                var fabric = new FabricInstaller(_http);
                // Installs the latest stable Fabric loader for this MC version.
                return await fabric.Install(instance.McVersion, launcher.MinecraftPath).ConfigureAwait(false);
            }
            case LoaderType.Forge:
                return await _forge.InstallForgeAsync(
                    instance.McVersion, minecraftDir, instance.JavaPath, log, ct).ConfigureAwait(false);

            case LoaderType.NeoForge:
                return await _forge.InstallNeoForgeAsync(
                    instance.McVersion, minecraftDir, instance.JavaPath, log, ct).ConfigureAwait(false);

            default:
                return instance.McVersion; // Vanilla: nothing to install.
        }
    }
}
