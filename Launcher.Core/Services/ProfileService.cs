// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Manages selectable offline profiles (name + skin/cape + body model), persisted
/// to <c>profiles.json</c>. Replaces the old "type a username every time" flow:
/// the user picks a saved profile to play and to edit its skin. No UI, no network.
/// </summary>
public sealed class ProfileService
{
    private readonly LauncherPaths _paths;

    public ProfileService(LauncherPaths paths) => _paths = paths;

    public async Task<IReadOnlyList<OfflineProfile>> ListAsync(CancellationToken ct = default)
    {
        var list = await JsonStore.ReadAsync<List<OfflineProfile>>(_paths.ProfilesFile, ct).ConfigureAwait(false)
                   ?? new List<OfflineProfile>();
        return list;
    }

    /// <summary>Returns the profiles, creating a default "Player" one if none exist yet.</summary>
    public async Task<IReadOnlyList<OfflineProfile>> ListEnsuredAsync(CancellationToken ct = default)
    {
        var list = (await ListAsync(ct).ConfigureAwait(false)).ToList();
        if (list.Count == 0)
        {
            list.Add(new OfflineProfile { Name = "Player" });
            await SaveAsync(list, ct).ConfigureAwait(false);
        }
        return list;
    }

    public async Task<OfflineProfile> CreateAsync(string name, bool slim = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Nome do perfil vazio.", nameof(name));

        var list = (await ListAsync(ct).ConfigureAwait(false)).ToList();
        var profile = new OfflineProfile { Name = name.Trim(), Slim = slim };
        list.Add(profile);
        await SaveAsync(list, ct).ConfigureAwait(false);
        return profile;
    }

    public async Task UpdateAsync(OfflineProfile profile, CancellationToken ct = default)
    {
        var list = (await ListAsync(ct).ConfigureAwait(false)).ToList();
        var idx = list.FindIndex(p => p.Id == profile.Id);
        if (idx < 0) throw new InvalidOperationException($"Perfil '{profile.Name}' nao existe.");
        list[idx] = profile;
        await SaveAsync(list, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var list = (await ListAsync(ct).ConfigureAwait(false)).ToList();
        var p = list.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        list.RemoveAll(x => x.Id == id);
        await SaveAsync(list, ct).ConfigureAwait(false);

        foreach (var f in new[] { p.SkinPath, p.CapePath })
            if (!string.IsNullOrEmpty(f) && File.Exists(f)) { try { File.Delete(f); } catch { } }
    }

    public Task<string> SetSkinAsync(OfflineProfile profile, string sourcePath, CancellationToken ct = default)
        => SetImageAsync(profile, sourcePath, isSkin: true, ct);

    public Task<string> SetCapeAsync(OfflineProfile profile, string sourcePath, CancellationToken ct = default)
        => SetImageAsync(profile, sourcePath, isSkin: false, ct);

    private async Task<string> SetImageAsync(OfflineProfile profile, string sourcePath, bool isSkin, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Imagem nao encontrada.", sourcePath);
        if (!IsPng(sourcePath))
            throw new InvalidOperationException(
                "O arquivo nao e um PNG valido (Minecraft so aceita PNG). Imagens WebP/JPG " +
                "renomeadas para .png nao funcionam — reexporte/baixe como PNG de verdade.");

        Directory.CreateDirectory(_paths.AppearanceDir);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var kind = isSkin ? "skin" : "cape";
        var dest = Path.Combine(_paths.AppearanceDir, $"{profile.Id}_{kind}{ext}");

        await using (var src = File.OpenRead(sourcePath))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        if (isSkin) profile.SkinPath = dest; else profile.CapePath = dest;
        await UpdateAsync(profile, ct).ConfigureAwait(false);
        return dest;
    }

    private Task SaveAsync(List<OfflineProfile> list, CancellationToken ct)
        => JsonStore.WriteAsync(_paths.ProfilesFile, list, ct);

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
