// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Resolves all on-disk locations. Root defaults to
/// <c>%APPDATA%/FURY Launcher</c> (cross-platform: the user's application-data
/// folder).
/// </summary>
public sealed class LauncherPaths
{
    public string Root { get; }

    public LauncherPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.DataFolderName);
        Directory.CreateDirectory(InstancesDir);
    }

    public string InstancesDir => Path.Combine(Root, "instances");
    public string InstancesIndex => Path.Combine(Root, "instances.json");

    /// <summary>Microsoft account/session cache used by CmlLib's login handler.</summary>
    public string AccountsFile => Path.Combine(Root, "accounts.json");

    /// <summary>Legacy selectable offline profiles (name + skin/cape + model). Kept only so
    /// the one-time account migration can read and then retire it (renamed to <c>.bak</c>).</summary>
    public string ProfilesFile => Path.Combine(Root, "profiles.json");

    /// <summary>Unified FURY account list (offline + Microsoft), keyed by our own account id.
    /// Distinct from <see cref="AccountsFile"/>, which stays the CmlLib token vault.</summary>
    public string FuryAccountsFile => Path.Combine(Root, "fury-accounts.json");

    /// <summary>On-disk cache of Microsoft (Mojang) skin PNGs + metadata, keyed by uuid.</summary>
    public string MsSkinCacheDir => Path.Combine(Root, "msskins");

    /// <summary>Small dismissible UI preferences.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Optional per-machine CurseForge API key (never committed).</summary>
    public string CurseForgeKeyFile => Path.Combine(Root, "curseforge.key");

    /// <summary>Root of launcher-managed Java runtimes; <c>java/&lt;major&gt;/bin/java.exe</c>.</summary>
    public string JavaDir => Path.Combine(Root, "java");

    /// <summary>Managed install folder for a given Java major version.</summary>
    public string JavaMajorDir(int major) => Path.Combine(JavaDir, major.ToString());

    /// <summary>Stored skin/cape image files.</summary>
    public string AppearanceDir => Path.Combine(Root, "appearances");

    public string InstanceDir(Instance i) => Path.Combine(InstancesDir, i.FolderName);
    public string InstanceConfig(Instance i) => Path.Combine(InstanceDir(i), "instance.json");
    public string InstanceMinecraft(Instance i) => Path.Combine(InstanceDir(i), ".minecraft");
    public string InstanceModsDir(Instance i) => Path.Combine(InstanceMinecraft(i), "mods");
    public string InstanceConfigDir(Instance i) => Path.Combine(InstanceMinecraft(i), "config");
    public string InstanceShaderpacksDir(Instance i) => Path.Combine(InstanceMinecraft(i), "shaderpacks");
    public string InstanceDatapacksDir(Instance i) => Path.Combine(InstanceMinecraft(i), "datapacks");

    /// <summary>The destination folder for a content kind (mods / shaderpacks / datapacks).</summary>
    public string InstanceContentDir(Instance i, ContentKind kind) => kind switch
    {
        ContentKind.Shader => InstanceShaderpacksDir(i),
        ContentKind.Datapack => InstanceDatapacksDir(i),
        _ => InstanceModsDir(i)
    };
}
