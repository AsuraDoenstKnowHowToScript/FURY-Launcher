// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Owns the unified account list (offline + Microsoft) persisted to
/// <c>fury-accounts.json</c>, plus the "active account" pointer stored in settings.
/// The active account is the one and only source of truth for who launches and whose
/// skin is applied — there is no parallel selection state anywhere else. On first use
/// it transparently migrates the legacy <c>profiles.json</c> via <see cref="AccountMigrator"/>.
/// No UI, no rendering.
/// </summary>
public sealed class AccountService
{
    private readonly LauncherPaths _paths;
    private readonly SettingsService _settings;
    private readonly AccountMigrator _migrator;

    public AccountService(LauncherPaths paths, SettingsService settings, IAuthManager auth)
    {
        _paths = paths;
        _settings = settings;
        _migrator = new AccountMigrator(paths, auth, settings);
    }

    /// <summary>All accounts, with <see cref="Account.IsActive"/> reconciled from settings.</summary>
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
    {
        await _migrator.EnsureMigratedAsync(ct).ConfigureAwait(false);
        var list = await ReadAsync(ct).ConfigureAwait(false);
        var activeId = (await _settings.LoadAsync(ct).ConfigureAwait(false)).ActiveAccountId;
        foreach (var a in list) a.IsActive = a.Id == activeId;
        return list;
    }

    /// <summary>The active account, or null when none is selected / the list is empty.</summary>
    public async Task<Account?> GetActiveAsync(CancellationToken ct = default)
    {
        var activeId = (await _settings.LoadAsync(ct).ConfigureAwait(false)).ActiveAccountId;
        if (string.IsNullOrEmpty(activeId)) return null;
        var list = await ReadAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault(a => a.Id == activeId);
    }

    /// <summary>Makes <paramref name="id"/> the active account and bumps its LastUsed.</summary>
    public async Task SetActiveAsync(string id, CancellationToken ct = default)
    {
        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        var acc = list.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Conta inexistente.");
        acc.LastUsed = DateTime.UtcNow;
        await WriteAsync(list, ct).ConfigureAwait(false);

        var s = await _settings.LoadAsync(ct).ConfigureAwait(false);
        s.ActiveAccountId = id;
        await _settings.SaveAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a new offline account with a unique (case-insensitive) nick.</summary>
    public async Task<Account> CreateOfflineAsync(string username, bool slim = false, CancellationToken ct = default)
    {
        var name = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("O nick não pode ficar vazio.", nameof(username));

        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        EnsureOfflineNickFree(list, name, exceptId: null);

        var acc = new Account
        {
            Username = name,
            Kind = AccountKind.Offline,
            Slim = slim,
            Uuid = OfflineIdentity.Uuid(name),
        };
        list.Add(acc);
        await WriteAsync(list, ct).ConfigureAwait(false);
        return acc;
    }

    /// <summary>
    /// Inserts or updates the Microsoft account identified by <paramref name="msAccountRef"/>
    /// (matched first by ref, then by uuid), refreshing its nick/uuid from a resumed session.
    /// </summary>
    public async Task<Account> UpsertMicrosoftAsync(
        string msAccountRef, string username, string uuid, CancellationToken ct = default)
    {
        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        var acc = list.FirstOrDefault(a => a.Kind == AccountKind.Microsoft && a.MsAccountRef == msAccountRef)
                  ?? (uuid.Length > 0 ? list.FirstOrDefault(a => a.Kind == AccountKind.Microsoft && a.Uuid == uuid) : null);
        if (acc == null)
        {
            acc = new Account { Kind = AccountKind.Microsoft, MsAccountRef = msAccountRef };
            list.Add(acc);
        }
        acc.MsAccountRef = msAccountRef;
        if (!string.IsNullOrWhiteSpace(username)) acc.Username = username;
        if (!string.IsNullOrWhiteSpace(uuid)) acc.Uuid = uuid;
        await WriteAsync(list, ct).ConfigureAwait(false);
        return acc;
    }

    /// <summary>Persists arbitrary edits to an account (skin path, slim, etc.).</summary>
    public async Task UpdateAsync(Account account, CancellationToken ct = default)
    {
        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        var idx = list.FindIndex(a => a.Id == account.Id);
        if (idx < 0) throw new InvalidOperationException($"Conta '{account.Username}' não existe.");
        list[idx] = account;
        await WriteAsync(list, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames an offline account's nick, keeping it unique and recomputing the offline UUID.
    /// The caller is responsible for migrating any per-instance CustomSkinLoader skin file
    /// (see <see cref="SkinApplyService.RenameLocalSkinAsync"/>) so it does not orphan.
    /// Returns the old nick so the caller can do that rename.
    /// </summary>
    public async Task<string> RenameOfflineAsync(string id, string newName, CancellationToken ct = default)
    {
        var name = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("O nick não pode ficar vazio.", nameof(newName));

        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        var acc = list.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Conta inexistente.");
        if (acc.Kind != AccountKind.Offline)
            throw new InvalidOperationException("Só contas offline podem ter o nick alterado aqui.");
        EnsureOfflineNickFree(list, name, exceptId: id);

        var old = acc.Username;
        acc.Username = name;
        acc.Uuid = OfflineIdentity.Uuid(name);
        await WriteAsync(list, ct).ConfigureAwait(false);
        return old;
    }

    /// <summary>
    /// Removes an account and its stored skin/cape assets. If it was active, promotes the
    /// most-recently-used remaining account (or clears the pointer when none are left).
    /// Microsoft sign-out is done by the caller (it needs the auth handler).
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var list = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
        var acc = list.FirstOrDefault(a => a.Id == id);
        if (acc == null) return;

        list.RemoveAll(a => a.Id == id);
        await WriteAsync(list, ct).ConfigureAwait(false);

        foreach (var f in new[] { acc.SkinPath, acc.CapePath })
            if (!string.IsNullOrEmpty(f) && File.Exists(f)) { try { File.Delete(f); } catch { } }

        var s = await _settings.LoadAsync(ct).ConfigureAwait(false);
        if (s.ActiveAccountId == id)
        {
            s.ActiveAccountId = list.OrderByDescending(a => a.LastUsed).FirstOrDefault()?.Id;
            await _settings.SaveAsync(s, ct).ConfigureAwait(false);
        }
    }

    public Task<string> SetSkinAsync(Account account, string sourcePath, CancellationToken ct = default)
        => SetImageAsync(account, sourcePath, isSkin: true, ct);

    public Task<string> SetCapeAsync(Account account, string sourcePath, CancellationToken ct = default)
        => SetImageAsync(account, sourcePath, isSkin: false, ct);

    // --- internals ---

    private async Task<string> SetImageAsync(Account account, string sourcePath, bool isSkin, CancellationToken ct)
    {
        if (account.Kind != AccountKind.Offline)
            throw new InvalidOperationException("Skins de conta Microsoft são gerenciadas pela Mojang.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Imagem não encontrada.", sourcePath);
        if (!IsPng(sourcePath))
            throw new InvalidOperationException(
                "O arquivo não é um PNG válido (Minecraft só aceita PNG). Imagens WebP/JPG " +
                "renomeadas para .png não funcionam; reexporte ou baixe como PNG de verdade.");

        Directory.CreateDirectory(_paths.AppearanceDir);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var kind = isSkin ? "skin" : "cape";
        var dest = Path.Combine(_paths.AppearanceDir, $"{account.Id}_{kind}{ext}");

        await using (var src = File.OpenRead(sourcePath))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        if (isSkin) account.SkinPath = dest; else account.CapePath = dest;
        await UpdateAsync(account, ct).ConfigureAwait(false);
        return dest;
    }

    private static void EnsureOfflineNickFree(IEnumerable<Account> list, string name, string? exceptId)
    {
        var clash = list.Any(a => a.Kind == AccountKind.Offline
                                  && a.Id != exceptId
                                  && string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase));
        if (clash)
            throw new InvalidOperationException(
                $"Já existe uma conta offline chamada '{name}'. O CustomSkinLoader indexa a skin pelo " +
                "nick, então nicks offline precisam ser únicos.");
    }

    private async Task<List<Account>> ReadAsync(CancellationToken ct)
        => await JsonStore.ReadAsync<List<Account>>(_paths.FuryAccountsFile, ct).ConfigureAwait(false)
           ?? new List<Account>();

    private Task WriteAsync(List<Account> list, CancellationToken ct)
        => JsonStore.WriteAsync(_paths.FuryAccountsFile, list, ct);

    /// <summary>True if the file starts with the 8-byte PNG signature.</summary>
    public static bool IsPng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) < 8) return false;
            return sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47
                && sig[4] == 0x0D && sig[5] == 0x0A && sig[6] == 0x1A && sig[7] == 0x0A;
        }
        catch { return false; }
    }
}
