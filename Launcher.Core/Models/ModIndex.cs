// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// Metadata remembered for one installed mod, keyed by its jar file name in the
/// sidecar index (<c>mods/.fury-index.json</c>). Captured at install time from
/// Modrinth so the list shows a real name, version and icon instead of the raw
/// file name.
/// </summary>
public sealed class ModIndexEntry
{
    public string? ProjectId { get; set; }
    public string? VersionId { get; set; }
    public string? Title { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }

    /// <summary>Remote icon URL (kept for reference / re-download).</summary>
    public string? IconUrl { get; set; }

    /// <summary>Local cached icon file name under <c>mods/.fury-cache/</c>, if downloaded.</summary>
    public string? IconFile { get; set; }
}

/// <summary>
/// Resolved, display-ready metadata for an installed mod. Produced from the index,
/// then the jar's own manifest, then a cleaned-up file name as a last resort.
/// </summary>
public sealed record ModDisplayInfo(string Title, string? Version, string? Description, string? IconPath);
