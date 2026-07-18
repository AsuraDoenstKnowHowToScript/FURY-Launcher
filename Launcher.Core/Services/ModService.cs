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

    /// <summary>Jars for mods, zips for shaders/datapacks.</summary>
    private static string ContentExtension(ContentKind kind) => kind == ContentKind.Mod ? ".jar" : ".zip";

    /// <summary>Lists the files of a content kind in its instance folder (enabled + disabled).</summary>
    public IReadOnlyList<ModItem> ListContent(Instance instance, ContentKind kind)
    {
        var dir = _paths.InstanceContentDir(instance, kind);
        if (!Directory.Exists(dir))
            return new List<ModItem>();

        var ext = ContentExtension(kind);
        return Directory.EnumerateFiles(dir)
            .Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(ext + ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ModItem { FileName = Path.GetFileName(p), FullPath = p })
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ModItem> ListMods(Instance instance) => ListContent(instance, ContentKind.Mod);

    /// <summary>Copies an external file into the instance's folder for that content kind.</summary>
    public async Task AddContentAsync(Instance instance, string sourcePath, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("File not found.", sourcePath);

        var dir = _paths.InstanceContentDir(instance, kind);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(sourcePath));

        await using var src = File.OpenRead(sourcePath);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    public Task AddModAsync(Instance instance, string sourceJarPath, CancellationToken ct = default)
        => AddContentAsync(instance, sourceJarPath, ContentKind.Mod, ct);

    public void RemoveMod(Instance instance, string fileName, ContentKind kind = ContentKind.Mod)
    {
        var path = Path.Combine(_paths.InstanceContentDir(instance, kind), fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Removes a content file and clears its install-index entry (so the search list
    /// stops marking it as installed).</summary>
    public async Task RemoveContentAsync(Instance instance, string fileName, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RemoveMod(instance, fileName, kind);
        await _metadata.RemoveFromIndexAsync(instance, fileName, kind).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables/disables an item by renaming between <c>.ext</c> and <c>.ext.disabled</c>.
    /// Returns the new file name so the caller can update state without a full reload.
    /// </summary>
    public string ToggleMod(Instance instance, string fileName, ContentKind kind = ContentKind.Mod)
    {
        var dir = _paths.InstanceContentDir(instance, kind);
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        string target = fileName.EndsWith(ModItem.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ModItem.DisabledSuffix.Length]                 // enable
            : fileName + ModItem.DisabledSuffix;                          // disable

        File.Move(path, Path.Combine(dir, target), overwrite: true);
        return target;
    }

    public Task<IReadOnlyList<ModrinthHit>> SearchModrinthAsync(
        Instance instance, string query, ContentKind kind = ContentKind.Mod, int offset = 0, CancellationToken ct = default)
        => _modrinth.SearchAsync(query, instance.McVersion, instance.Loader, kind, offset, ct);

    /// <summary>Lists the versions of a project compatible with the instance (for the version chooser).</summary>
    public Task<IReadOnlyList<ModrinthVersion>> GetModrinthVersionsAsync(
        Instance instance, string projectId, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
        => _modrinth.GetVersionsAsync(projectId, instance.McVersion, instance.Loader, kind, ct);

    /// <summary>Installs the newest compatible version of a project (plus required deps, for mods).</summary>
    public async Task<IReadOnlyList<string>> InstallFromModrinthAsync(
        Instance instance, string projectId, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        var version = await _modrinth.GetCompatibleVersionAsync(projectId, instance.McVersion, instance.Loader, kind, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No compatible version was found for {instance.McVersion}.");
        return await InstallVersionAsync(instance, version, kind, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a specific version into the folder for its content kind. Mods also pull
    /// every required dependency and record metadata; shaders/datapacks are a single file.
    /// Returns the file names that were downloaded.
    /// </summary>
    public async Task<IReadOnlyList<string>> InstallVersionAsync(
        Instance instance, ModrinthVersion version, ContentKind kind = ContentKind.Mod,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (kind == ContentKind.Mod && instance.Loader == LoaderType.Vanilla)
            throw new InvalidOperationException("Vanilla instances do not support mods.");

        var targetDir = _paths.InstanceContentDir(instance, kind);
        Directory.CreateDirectory(targetDir);
        var installed = new List<string>();

        if (kind == ContentKind.Mod)
        {
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // project ids already done
            await InstallWithDepsAsync(instance, version, targetDir, installed, handled, log, ct).ConfigureAwait(false);
        }
        else
        {
            // Shaders/datapacks: just the .zip file, no dependency graph.
            var path = await _modrinth.DownloadAsync(version, targetDir, kind, ct).ConfigureAwait(false);
            var name = Path.GetFileName(path);
            installed.Add(name);
            log?.Report(name);
            // Record name/icon so the installed list shows them nicely (kind-scoped index).
            await _metadata.RecordInstallAsync(instance, name, version, kind, ct).ConfigureAwait(false);
        }

        return installed;
    }

    private async Task InstallWithDepsAsync(
        Instance instance, ModrinthVersion version, string modsDir,
        List<string> installed, HashSet<string> handled, IProgress<string>? log, CancellationToken ct)
    {
        // De-dupe by project so a shared dependency (e.g. Fabric API) is installed once.
        if (version.ProjectId != null && !handled.Add(version.ProjectId))
            return;

        var path = await _modrinth.DownloadAsync(version, modsDir, ContentKind.Mod, ct).ConfigureAwait(false);
        var name = Path.GetFileName(path);
        installed.Add(name);
        log?.Report(name);

        // Remember the pretty name/version/icon so the list never shows the raw file name.
        await _metadata.RecordInstallAsync(instance, name, version, ContentKind.Mod, ct).ConfigureAwait(false);

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
                var list = await _modrinth.GetVersionsAsync(dep.ProjectId, instance.McVersion, instance.Loader, ContentKind.Mod, ct).ConfigureAwait(false);
                depVersion = list.FirstOrDefault();
            }

            if (depVersion != null)
                await InstallWithDepsAsync(instance, depVersion, modsDir, installed, handled, log, ct).ConfigureAwait(false);
        }
    }
}
