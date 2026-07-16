// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Applies a profile's skin/cape in-game for an <b>offline</b> player by setting
/// up the CustomSkinLoader mod with a LocalSkin entry. Vanilla Minecraft cannot
/// show a custom skin on an offline (non-paid) account, so this is the only real
/// path: it installs CustomSkinLoader into the instance and drops the skin under
/// <c>LocalSkin/(skins|capes)/{name}.png</c>, keyed by the profile's own name.
///
/// Microsoft accounts get their skin from Mojang and would need an upload flow
/// instead (not implemented here).
/// </summary>
public sealed class SkinApplyService
{
    // Modrinth slug for CustomSkinLoader.
    private const string CslSlug = "customskinloader";

    private readonly LauncherPaths _paths;
    private readonly ModService _mods;

    public SkinApplyService(LauncherPaths paths, ModService mods)
    {
        _paths = paths;
        _mods = mods;
    }

    /// <summary>
    /// Installs CustomSkinLoader (if missing) and places the profile's skin/cape so
    /// they render in-game for the offline player named after the profile.
    /// </summary>
    /// <returns>The username the skin was applied for (matches the profile name).</returns>
    public async Task<string> ApplyOfflineAsync(
        Instance instance, OfflineProfile profile, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        var name = (profile.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Perfil sem nome.", nameof(profile));

        if (instance.Loader == LoaderType.Vanilla)
            throw new InvalidOperationException(
                "Skin em conta offline precisa do mod CustomSkinLoader, que exige um loader " +
                "(Forge/Fabric/NeoForge). Esta instancia e Vanilla; crie ou edite para usar um loader.");

        var hasSkin = profile.SkinPath != null && File.Exists(profile.SkinPath);
        var hasCape = profile.CapePath != null && File.Exists(profile.CapePath);
        if (!hasSkin && !hasCape)
            throw new InvalidOperationException(
                $"O perfil '{name}' nao tem skin nem capa. Escolha uma skin PNG primeiro nesta aba.");

        // 1) Ensure the CustomSkinLoader mod is in the instance.
        await EnsureCustomSkinLoaderAsync(instance, log, ct).ConfigureAwait(false);

        // 2) Drop the local skin/cape named by the in-game username.
        var localSkin = Path.Combine(_paths.InstanceMinecraft(instance), "CustomSkinLoader", "LocalSkin");
        if (hasSkin)
        {
            CopyInto(Path.Combine(localSkin, "skins"), name + ".png", profile.SkinPath!);
            log?.Report($"[skin] Skin local colocada para '{name}' (modelo {(profile.Slim ? "slim" : "classico")}).");
        }
        if (hasCape)
        {
            if (ProfileService.IsPng(profile.CapePath!))
            {
                CopyInto(Path.Combine(localSkin, "capes"), name + ".png", profile.CapePath!);
                log?.Report($"[skin] Capa local colocada para '{name}'.");
            }
            else
            {
                log?.Report("[skin] Capa ignorada: nao e um PNG valido (troque por um PNG de verdade).");
            }
        }

        // 3) Write a LocalSkin-first config so the local skin always wins over Mojang.
        WriteLocalSkinConfig(instance, profile.Slim);

        log?.Report("[skin] Pronto. Selecione este perfil ao jogar offline e (re)inicie a instancia.");
        return name;
    }

    private async Task EnsureCustomSkinLoaderAsync(Instance instance, IProgress<string>? log, CancellationToken ct)
    {
        var modsDir = _paths.InstanceModsDir(instance);
        if (Directory.Exists(modsDir) && Directory.EnumerateFiles(modsDir).Any(f =>
                Path.GetFileName(f).StartsWith("CustomSkinLoader", StringComparison.OrdinalIgnoreCase)))
        {
            log?.Report("[skin] CustomSkinLoader ja instalado nesta instancia.");
            return;
        }

        log?.Report("[skin] Baixando CustomSkinLoader (Modrinth, compativel com a versao/loader)...");
        var installed = await _mods.InstallFromModrinthAsync(instance, CslSlug, ct).ConfigureAwait(false);
        log?.Report("[skin] Instalado: " + string.Join(", ", installed));
    }

    private static void CopyInto(string dir, string fileName, string source)
    {
        Directory.CreateDirectory(dir);
        File.Copy(source, Path.Combine(dir, fileName), overwrite: true);
    }

    /// <summary>
    /// Writes <c>CustomSkinLoader/CustomSkinLoader.json</c> with only the LocalSkin
    /// loader, so an offline name never gets a stray Mojang skin instead of the local
    /// one. The default model (slim/classic) is set as a fallback for when the skin's
    /// own metadata is ambiguous. CSL migrates the version on load but keeps this loadlist.
    /// </summary>
    private void WriteLocalSkinConfig(Instance instance, bool slim)
    {
        var cslDir = Path.Combine(_paths.InstanceMinecraft(instance), "CustomSkinLoader");
        Directory.CreateDirectory(cslDir);
        var configFile = Path.Combine(cslDir, "CustomSkinLoader.json");

        var model = slim ? "slim" : "default";
        var config =
            "{\n" +
            "  \"version\": 1,\n" +
            "  \"enable\": true,\n" +
            "  \"enableSkull\": true,\n" +
            "  \"enableCape\": true,\n" +
            "  \"enableElytra\": true,\n" +
            "  \"enableDynamicSkin\": true,\n" +
            "  \"forceLegacySkin\": false,\n" +
            $"  \"defaultModel\": \"{model}\",\n" +
            "  \"loadlist\": [\n" +
            "    { \"name\": \"LocalSkin\", \"type\": \"LegacyLocalSkinLoader\", \"checkPNG\": true }\n" +
            "  ]\n" +
            "}\n";
        File.WriteAllText(configFile, config);
    }
}
