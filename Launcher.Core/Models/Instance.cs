// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// A launcher instance: an isolated .minecraft with its own version, loader,
/// memory and Java configuration. Serialized to <c>instance.json</c> per folder
/// and mirrored in the <c>instances.json</c> index. Pure data — no UI, no CmlLib.
/// </summary>
public sealed class Instance
{
    /// <summary>Stable unique id (also used to build the folder name).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name chosen by the user.</summary>
    public string Name { get; set; } = "";

    /// <summary>Minecraft version, e.g. <c>1.20.1</c>.</summary>
    public string McVersion { get; set; } = "";

    public LoaderType Loader { get; set; } = LoaderType.Vanilla;

    /// <summary>
    /// Resolved launch version id for the loader (e.g. <c>fabric-loader-0.15.0-1.20.1</c>).
    /// Null until the loader is installed; filled in on first launch.
    /// </summary>
    public string? LoaderVersion { get; set; }

    public int MinRamMb { get; set; } = 512;
    public int MaxRamMb { get; set; } = 2048;

    /// <summary>Extra JVM arguments as a single space-separated string (edited raw in the UI).</summary>
    public string JvmArgs { get; set; } = "";

    /// <summary>Absolute path to a Java executable, or null to let CmlLib manage the runtime.</summary>
    public string? JavaPath { get; set; }

    /// <summary>Folder name under <c>instances/</c> (sanitized from the name + id).</summary>
    public string FolderName { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The actual version id to install/launch: the loader version when modded, else the MC version.</summary>
    public string LaunchVersionId =>
        Loader == LoaderType.Vanilla || string.IsNullOrEmpty(LoaderVersion)
            ? McVersion
            : LoaderVersion;
}
