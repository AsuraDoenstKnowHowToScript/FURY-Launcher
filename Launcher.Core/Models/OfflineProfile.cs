// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// A saved offline identity the user selects to play (no need to retype a name
/// every time). Carries its own skin/cape and body model. Skins are client-side
/// only (via CustomSkinLoader); they are not shown to other players.
/// </summary>
public sealed class OfflineProfile
{
    /// <summary>Stable id (used to name the stored skin/cape files).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>In-game offline username.</summary>
    public string Name { get; set; } = "";

    /// <summary>True = slim/Alex arms; false = classic/Steve.</summary>
    public bool Slim { get; set; }

    /// <summary>Absolute path to the stored skin PNG, or null.</summary>
    public string? SkinPath { get; set; }

    /// <summary>Absolute path to the stored cape PNG, or null.</summary>
    public string? CapePath { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
