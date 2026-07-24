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
    /// Installs CustomSkinLoader (if missing) and places the account's skin/cape so they render
    /// in-game for the offline player named after the account. Only offline accounts are supported —
    /// Microsoft skins come from Mojang and cannot be set through CustomSkinLoader.
    /// </summary>
    /// <returns>The username the skin was applied for (matches the account nick).</returns>
    public Task<string> ApplyOfflineAsync(
        Instance instance, Account account, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));
        if (account.Kind != AccountKind.Offline)
            throw new InvalidOperationException("Skins de conta Microsoft são gerenciadas pela Mojang, não pelo CustomSkinLoader.");
        return ApplyCoreAsync(instance, account.Username, account.Slim, account.SkinPath, account.CapePath, log, ct);
    }

    /// <summary>
    /// Renames an offline account's local skin/cape files (keyed by nick) across every given
    /// instance, so a nick change does not leave the old skin orphaned in CustomSkinLoader.
    /// Best-effort per instance; a missing file is simply skipped.
    /// </summary>
    public Task RenameLocalSkinAsync(
        IEnumerable<Instance> instances, string oldName, string newName, CancellationToken ct = default)
    {
        var oldN = (oldName ?? "").Trim();
        var newN = (newName ?? "").Trim();
        if (oldN.Length == 0 || newN.Length == 0 || string.Equals(oldN, newN, StringComparison.Ordinal))
            return Task.CompletedTask;

        foreach (var instance in instances)
        {
            var localSkin = Path.Combine(_paths.InstanceMinecraft(instance), "CustomSkinLoader", "LocalSkin");
            foreach (var sub in new[] { "skins", "capes" })
            {
                try
                {
                    var from = Path.Combine(localSkin, sub, oldN + ".png");
                    if (!File.Exists(from)) continue;
                    var to = Path.Combine(localSkin, sub, newN + ".png");
                    File.Move(from, to, overwrite: true);
                }
                catch (Exception ex) { CrashLog.Write($"[skin] renaming local {sub} '{oldN}'->'{newN}' failed", ex); }
            }
        }
        return Task.CompletedTask;
    }

    private async Task<string> ApplyCoreAsync(
        Instance instance, string username, bool slim, string? skinPath, string? capePath,
        IProgress<string>? log, CancellationToken ct)
    {
        var name = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Conta sem nick.", nameof(username));

        if (instance.Loader == LoaderType.Vanilla)
            throw new InvalidOperationException(
                "Skin em conta offline precisa do mod CustomSkinLoader, que exige um loader " +
                "(Forge/Fabric/NeoForge). Esta instancia e Vanilla; crie ou edite para usar um loader.");

        var hasSkin = skinPath != null && File.Exists(skinPath);
        var hasCape = capePath != null && File.Exists(capePath);
        if (!hasSkin && !hasCape)
            throw new InvalidOperationException(
                $"A conta '{name}' nao tem skin nem capa. Escolha uma skin PNG primeiro nesta aba.");

        // 1) Ensure the CustomSkinLoader mod is in the instance.
        await EnsureCustomSkinLoaderAsync(instance, log, ct).ConfigureAwait(false);

        // 2) Drop the local skin/cape named by the in-game username.
        var localSkin = Path.Combine(_paths.InstanceMinecraft(instance), "CustomSkinLoader", "LocalSkin");
        if (hasSkin)
        {
            CopyInto(Path.Combine(localSkin, "skins"), name + ".png", skinPath!);
            log?.Report($"[skin] Skin local colocada para '{name}' (modelo {(slim ? "slim" : "classico")}).");
        }
        if (hasCape)
        {
            if (AccountService.IsPng(capePath!))
            {
                CopyInto(Path.Combine(localSkin, "capes"), name + ".png", capePath!);
                log?.Report($"[skin] Capa local colocada para '{name}'.");
            }
            else
            {
                log?.Report("[skin] Capa ignorada: nao e um PNG valido (troque por um PNG de verdade).");
            }
        }

        // 3) Write a LocalSkin-first config so the local skin always wins over Mojang.
        WriteLocalSkinConfig(instance, slim);

        log?.Report("[skin] Pronto. Selecione esta conta ao jogar offline e (re)inicie a instancia.");
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
        var installed = await _mods.InstallFromModrinthAsync(instance, CslSlug, ContentKind.Mod, ct).ConfigureAwait(false);
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
