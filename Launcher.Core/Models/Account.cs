// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// A single playable identity. Unifies what used to be an "offline profile" and a
/// "Microsoft account" into one record: an offline account carries its own
/// skin/cape/model (client-side, via CustomSkinLoader), a Microsoft account carries
/// a reference into CmlLib's token cache and a Mojang-managed (read-only) skin.
/// The active account is the single source of truth for who launches; it is tracked
/// by <see cref="LauncherSettings.ActiveAccountId"/>, never by parallel UI state.
/// </summary>
public sealed class Account
{
    /// <summary>Stable FURY-side id (used to name stored skin/cape files).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>In-game nick. For offline it IS the identity; for Microsoft it mirrors the Mojang name.</summary>
    public string Username { get; set; } = "";

    /// <summary>Offline (deterministic UUID) or Microsoft (Xbox/OAuth).</summary>
    public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>Offline: derived from the name. Microsoft: taken from the resumed session.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>
    /// For Microsoft accounts: <c>IXboxGameAccount.Identifier</c> in CmlLib's cache, used to
    /// resume/sign-out this specific account. Null for offline accounts.
    /// </summary>
    public string? MsAccountRef { get; set; }

    /// <summary>Absolute path to the stored skin PNG. Null on Microsoft accounts (Mojang-managed).</summary>
    public string? SkinPath { get; set; }

    /// <summary>Absolute path to the stored cape PNG. Null on Microsoft accounts.</summary>
    public string? CapePath { get; set; }

    /// <summary>True = slim/Alex arms; false = classic/Steve.</summary>
    public bool Slim { get; set; }

    /// <summary>
    /// Convenience mirror of "this is the active account", reconciled from
    /// <see cref="LauncherSettings.ActiveAccountId"/> at list time. Not a second source of truth.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Last time this account was selected/launched; used to pick a fallback active account.</summary>
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
