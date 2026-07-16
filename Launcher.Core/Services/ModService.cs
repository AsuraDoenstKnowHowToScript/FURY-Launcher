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
    private readonly ModMetadataService _metadata;

    public ModService(LauncherPaths paths, ModrinthClient modrinth, ModMetadataService metadata)
    {
        _paths = paths;
        _modrinth = modrinth;
        _metadata = metadata;
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

    public Task<IReadOnlyList<ModrinthHit>> SearchModrinthAsync(Instance instance, string query, int offset = 0, CancellationToken ct = default)
        => _modrinth.SearchAsync(query, instance.McVersion, instance.Loader, offset, ct);

    /// <summary>Lists the mod versions compatible with the instance (for the version chooser).</summary>
    public Task<IReadOnlyList<ModrinthVersion>> GetModrinthVersionsAsync(Instance instance, string projectId, CancellationToken ct = default)
        => _modrinth.GetVersionsAsync(projectId, instance.McVersion, instance.Loader, ct);

    /// <summary>Installs the newest compatible version of a project (plus its required dependencies).</summary>
    public async Task<IReadOnlyList<string>> InstallFromModrinthAsync(Instance instance, string projectId, CancellationToken ct = default)
    {
        var version = await _modrinth.GetCompatibleVersionAsync(projectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No version of this mod is compatible with {instance.McVersion} + {instance.Loader}.");
        return await InstallVersionAsync(instance, version, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a specific mod version and, recursively, every REQUIRED dependency
    /// (resolved to a version compatible with the instance). Optional/incompatible
    /// dependencies are ignored. Returns the file names that were downloaded.
    /// </summary>
    public async Task<IReadOnlyList<string>> InstallVersionAsync(
        Instance instance, ModrinthVersion version, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (instance.Loader == LoaderType.Vanilla)
            throw new InvalidOperationException("Vanilla instances do not support mods.");

        var modsDir = _paths.InstanceModsDir(instance);
        var installed = new List<string>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // project ids already done
        await InstallWithDepsAsync(instance, version, modsDir, installed, handled, log, ct).ConfigureAwait(false);
        return installed;
    }

    private async Task InstallWithDepsAsync(
        Instance instance, ModrinthVersion version, string modsDir,
        List<string> installed, HashSet<string> handled, IProgress<string>? log, CancellationToken ct)
    {
        // De-dupe by project so a shared dependency (e.g. Fabric API) is installed once.
        if (version.ProjectId != null && !handled.Add(version.ProjectId))
            return;

        var path = await _modrinth.DownloadAsync(version, modsDir, ct).ConfigureAwait(false);
        var name = Path.GetFileName(path);
        installed.Add(name);
        log?.Report(name);

        // Remember the pretty name/version/icon so the list never shows the raw file name.
        await _metadata.RecordInstallAsync(instance, name, version, ct).ConfigureAwait(false);

        foreach (var dep in version.Dependencies ?? new List<ModrinthDependency>())
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(dep.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                continue; // only auto-install required deps

            ModrinthVersion? depVersion = null;
            if (!string.IsNullOrEmpty(dep.VersionId))
                depVersion = await _modrinth.GetVersionByIdAsync(dep.VersionId, ct).ConfigureAwait(false);
            else if (!string.IsNullOrEmpty(dep.ProjectId))
            {
                if (handled.Contains(dep.ProjectId)) continue;
                var list = await _modrinth.GetVersionsAsync(dep.ProjectId, instance.McVersion, instance.Loader, ct).ConfigureAwait(false);
                depVersion = list.FirstOrDefault();
            }

            if (depVersion != null)
                await InstallWithDepsAsync(instance, depVersion, modsDir, installed, handled, log, ct).ConfigureAwait(false);
        }
    }
}
