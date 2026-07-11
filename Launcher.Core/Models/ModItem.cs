// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>A single jar in an instance's <c>mods/</c> folder.</summary>
public sealed class ModItem
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>File name on disk, e.g. <c>sodium.jar</c> or <c>sodium.jar.disabled</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute path on disk.</summary>
    public required string FullPath { get; init; }

    /// <summary>True when the jar is active (not renamed to <c>.disabled</c>).</summary>
    public bool Enabled => !FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>File name without the <c>.disabled</c> suffix, for display.</summary>
    public string DisplayName => Enabled
        ? FileName
        : FileName[..^DisabledSuffix.Length];
}
