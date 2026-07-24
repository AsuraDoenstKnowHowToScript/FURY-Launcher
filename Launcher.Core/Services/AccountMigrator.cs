// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// One-time, idempotent migration from the legacy <c>profiles.json</c> (offline profiles)
/// to the unified <c>fury-accounts.json</c>. Each old profile becomes an offline
/// <see cref="Account"/> (id/name/slim/skin/cape preserved); every Microsoft account already
/// cached by CmlLib is imported as a Microsoft <see cref="Account"/> shell (nick/uuid filled
/// on the first successful silent resume). The legacy file is renamed to <c>.bak</c>, never
/// deleted. Guarded by the existence of <c>fury-accounts.json</c>, so it runs at most once.
/// </summary>
public sealed class AccountMigrator
{
    private readonly LauncherPaths _paths;
    private readonly IAuthManager _auth;
    private readonly SettingsService _settings;

    public AccountMigrator(LauncherPaths paths, IAuthManager auth, SettingsService settings)
    {
        _paths = paths;
        _auth = auth;
        _settings = settings;
    }

    public async Task EnsureMigratedAsync(CancellationToken ct = default)
    {
        // Already migrated (or a fresh install that wrote the file): nothing to do.
        if (File.Exists(_paths.FuryAccountsFile)) return;

        var accounts = new List<Account>();

        // 1) Legacy offline profiles → offline accounts. Read through a self-contained DTO so
        //    the old OfflineProfile type can be removed with the rest of the profile stack.
        try
        {
            var legacy = await JsonStore.ReadAsync<List<LegacyProfile>>(_paths.ProfilesFile, ct).ConfigureAwait(false);
            if (legacy != null)
            {
                foreach (var p in legacy)
                {
                    var name = (p.Name ?? "").Trim();
                    accounts.Add(new Account
                    {
                        Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                        Username = name,
                        Kind = AccountKind.Offline,
                        Slim = p.Slim,
                        SkinPath = p.SkinPath,
                        CapePath = p.CapePath,
                        Uuid = string.IsNullOrEmpty(name) ? "" : OfflineIdentity.Uuid(name),
                        CreatedUtc = p.CreatedUtc == default ? DateTime.UtcNow : p.CreatedUtc,
                        LastUsed = p.CreatedUtc == default ? DateTime.UtcNow : p.CreatedUtc,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("[migrate] reading legacy profiles.json failed", ex);
        }

        // 2) Cached Microsoft accounts → Microsoft account shells (details fill on first resume).
        try
        {
            foreach (var reff in _auth.ListMicrosoftAccountRefs())
                accounts.Add(new Account { Kind = AccountKind.Microsoft, MsAccountRef = reff });
        }
        catch (Exception ex)
        {
            CrashLog.Write("[migrate] importing cached Microsoft accounts failed", ex);
        }

        // 3) Persist the unified list.
        await JsonStore.WriteAsync(_paths.FuryAccountsFile, accounts, ct).ConfigureAwait(false);

        // 4) Pick the active account. The old OfflineProfileCombo selection was never persisted,
        //    so per the agreed fallback we take the first account (empty list → null).
        var s = await _settings.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(s.ActiveAccountId))
        {
            s.ActiveAccountId = accounts.FirstOrDefault()?.Id;
            await _settings.SaveAsync(s, ct).ConfigureAwait(false);
        }

        // 5) Retire the legacy file (rename, never delete) so we do not migrate twice.
        try
        {
            if (File.Exists(_paths.ProfilesFile))
            {
                var bak = _paths.ProfilesFile + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(_paths.ProfilesFile, bak);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("[migrate] retiring legacy profiles.json failed", ex);
        }
    }

    /// <summary>Shape of the legacy <c>profiles.json</c> entries, for one-time reading only.</summary>
    private sealed class LegacyProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Slim { get; set; }
        public string? SkinPath { get; set; }
        public string? CapePath { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
