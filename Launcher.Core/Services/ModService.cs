// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Per-instance mod management: list the <c>mods/</c> folder, add/remove jars,
/// toggle enabled state (<c>.jar</c> ↔ <c>.jar.disabled</c>), and install from
/// Modrinth filtered to the instance's MC version + loader.
/// </summary>
public sealed class ModService
{
    private readonly LauncherPaths _paths;
    private readonly ModrinthClient _modrinth;

    public ModService(LauncherPaths paths, ModrinthClient modrinth)
    {
        _paths = paths;
        _modrinth = modrinth;
    }

    public IReadOnlyList<ModItem> ListMods(Instance instance)
    {
        var dir = _paths.InstanceModsDir(instance);
        if (!Directory.Exists(dir))
            return new List<ModItem>();

        return Directory.EnumerateFiles(dir)
            .Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".jar" + ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ModItem { FileName = Path.GetFileName(p), FullPath = p })
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Copies an external jar into the instance's mods folder.</summary>
    public async Task AddModAsync(Instance instance, string sourceJarPath, CancellationToken ct = default)
    {
        if (!File.Exists(sourceJarPath))
            throw new FileNotFoundException("Mod jar not found.", sourceJarPath);

        var dir = _paths.InstanceModsDir(instance);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(sourceJarPath));

        await using var src = File.OpenRead(sourceJarPath);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    public void RemoveMod(Instance instance, string fileName)
    {
        var path = Path.Combine(_paths.InstanceModsDir(instance), fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Enables/disables a mod by renaming between <c>.jar</c> and <c>.jar.disabled</c>.</summary>
    public void ToggleMod(Instance instance, string fileName)
    {
        var dir = _paths.InstanceModsDir(instance);
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Mod not found.", path);

        string target = fileName.EndsWith(ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ModItem.DisabledSuffix.Length]                 // enable
            : fileName + ModItem.DisabledSuffix;                          // disable

        File.Move(path, Path.Combine(dir, target), overwrite: true);
    }

    public Task<IReadOnlyList<ModrinthHit>> SearchModrinthAsync(Instance instance, string query, CancellationToken ct = default)
        => _modrinth.SearchAsync(query, instance.McVersion, instance.Loader, ct);

    /// <summary>Resolves the compatible version of a Modrinth project and downloads it into the instance.</summary>
    public async Task<string> InstallFromModrinthAsync(Instance instance, string projectId, CancellationToken ct = default)
    {
        var version = await _modrinth.GetCompatibleVersionAsync(projectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No version of this mod is compatible with {instance.McVersion} + {instance.Loader}.");

        return await _modrinth.DownloadAsync(version, _paths.InstanceModsDir(instance), ct).ConfigureAwait(false);
    }
}
