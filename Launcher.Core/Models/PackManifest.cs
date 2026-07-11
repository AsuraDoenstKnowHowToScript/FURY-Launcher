// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// The <c>fury.json</c> manifest inside a <c>.frpack</c> (FURY Package). Fully
/// describes an instance so a friend can import and play without any external
/// service: the <c>mods/</c> jars travel inside the package itself.
/// </summary>
public sealed class PackManifest
{
    /// <summary>Bumped when the on-disk format changes; import warns if newer.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public LoaderType Loader { get; set; } = LoaderType.Vanilla;

    /// <summary>Loader build recorded for reference (import reinstalls the loader).</summary>
    public string? LoaderVersion { get; set; }

    public int MinRamMb { get; set; } = 512;
    public int MaxRamMb { get; set; } = 2048;
    public string JvmArgs { get; set; } = "";

    /// <summary>Mod jar file names bundled under <c>mods/</c> in the package.</summary>
    public List<string> Mods { get; set; } = new();

    public string CreatedBy { get; set; } = AppInfo.Name;
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A read-only look at a <c>.frpack</c> before importing it.</summary>
public sealed class PackPreview
{
    public required PackManifest Manifest { get; init; }

    /// <summary>Human-readable problems found; empty means clean.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
