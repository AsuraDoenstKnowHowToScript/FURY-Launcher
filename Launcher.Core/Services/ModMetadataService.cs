// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Resolves a friendly name, version and icon for each installed mod instead of the
/// raw jar file name. Metadata comes, in order of trust, from the sidecar index
/// written at Modrinth install time (<c>mods/.fury-index.json</c>), then the jar's
/// own manifest (Fabric/Quilt/Forge/NeoForge), then a cleaned-up file name. Icons
/// are cached under <c>mods/.fury-cache/</c>. No UI dependency.
/// </summary>
public sealed class ModMetadataService
{
    private const string IndexFile = ".fury-index.json";
    private const string CacheFolder = ".fury-cache";

    private readonly LauncherPaths _paths;
    private readonly ModrinthClient _modrinth;

    public ModMetadataService(LauncherPaths paths, ModrinthClient modrinth)
    {
        _paths = paths;
        _modrinth = modrinth;
    }

    private string IndexPath(Instance instance) => Path.Combine(_paths.InstanceModsDir(instance), IndexFile);
    private static string CacheDir(string modsDir) => Path.Combine(modsDir, CacheFolder);

    /// <summary>Loads the per-instance metadata index (file name → entry), case-insensitive.</summary>
    public async Task<Dictionary<string, ModIndexEntry>> LoadIndexAsync(Instance instance)
    {
        var raw = await JsonStore.ReadAsync<Dictionary<string, ModIndexEntry>>(IndexPath(instance)).ConfigureAwait(false);
        return raw == null
            ? new Dictionary<string, ModIndexEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModIndexEntry>(raw, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remembers metadata for a mod just installed from Modrinth: fetches the project
    /// (title, description, icon), caches the icon locally, and records it in the index.
    /// Best-effort: any failure is logged and skipped so the install itself never breaks.
    /// </summary>
    public async Task RecordInstallAsync(Instance instance, string fileName, ModrinthVersion version, CancellationToken ct = default)
    {
        try
        {
            var index = await LoadIndexAsync(instance).ConfigureAwait(false);
            var entry = new ModIndexEntry
            {
                ProjectId = version.ProjectId,
                VersionId = version.Id,
                Version = version.VersionNumber,
                Title = string.IsNullOrWhiteSpace(version.Name) ? null : version.Name
            };

            if (!string.IsNullOrEmpty(version.ProjectId))
            {
                var project = await _modrinth.GetProjectAsync(version.ProjectId, ct).ConfigureAwait(false);
                if (project != null)
                {
                    if (!string.IsNullOrWhiteSpace(project.Title)) entry.Title = project.Title;
                    entry.Description = project.Description;
                    entry.IconUrl = project.IconUrl;
                    if (!string.IsNullOrWhiteSpace(project.IconUrl))
                        entry.IconFile = await TryCacheIconAsync(instance, version.ProjectId!, project.IconUrl!, ct).ConfigureAwait(false);
                }
            }

            index[fileName] = entry;
            await JsonStore.WriteAsync(IndexPath(instance), index).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { CrashLog.Write("[mods] recording install metadata failed", ex); }
    }

    /// <summary>
    /// Resolves display metadata for one installed mod, trying the index, then the jar
    /// manifest, then a cleaned file name. Does jar I/O, so call it off the UI thread.
    /// </summary>
    public ModDisplayInfo Resolve(Instance instance, ModItem item, IReadOnlyDictionary<string, ModIndexEntry> index)
    {
        var modsDir = _paths.InstanceModsDir(instance);
        var key = item.DisplayName; // the enabled file name (index is keyed by that)

        if (index.TryGetValue(key, out var e) && !string.IsNullOrWhiteSpace(e.Title))
        {
            string? icon = null;
            if (!string.IsNullOrEmpty(e.IconFile))
            {
                var p = Path.Combine(CacheDir(modsDir), e.IconFile);
                if (File.Exists(p)) icon = p;
            }
            // No cached Modrinth icon (project had none, or it failed to download):
            // fall back to whatever icon the jar itself ships.
            icon ??= TryReadJar(item.FullPath, CacheDir(modsDir))?.IconPath;
            return new ModDisplayInfo(e.Title!, e.Version, e.Description, icon);
        }

        var fromJar = TryReadJar(item.FullPath, CacheDir(modsDir));
        if (fromJar != null) return fromJar;

        return new ModDisplayInfo(CleanName(key), null, null, null);
    }

    private async Task<string?> TryCacheIconAsync(Instance instance, string projectId, string url, CancellationToken ct)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
            var fileName = Sanitize(projectId) + ext;
            var dest = Path.Combine(CacheDir(_paths.InstanceModsDir(instance)), fileName);
            await _modrinth.DownloadFileAsync(url, dest, ct).ConfigureAwait(false);
            return fileName;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // -------- jar manifest parsing (Fabric / Quilt / Forge / NeoForge) --------

    private static ModDisplayInfo? TryReadJar(string jarPath, string cacheDir)
    {
        try
        {
            if (!File.Exists(jarPath)) return null;
            using var zip = ZipFile.OpenRead(jarPath);

            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric != null)
            {
                using var s = fabric.Open();
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                var name = GetStr(root, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    return new ModDisplayInfo(name!, CleanVersion(GetStr(root, "version")),
                        GetStr(root, "description"), ExtractIcon(zip, GetStr(root, "icon"), cacheDir, jarPath));
            }

            var quilt = zip.GetEntry("quilt.mod.json");
            if (quilt != null)
            {
                using var s = quilt.Open();
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("quilt_loader", out var ql))
                {
                    string? name = null, desc = null, iconRel = null;
                    if (ql.TryGetProperty("metadata", out var meta))
                    {
                        name = GetStr(meta, "name");
                        desc = GetStr(meta, "description");
                        iconRel = GetStr(meta, "icon");
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                        return new ModDisplayInfo(name!, CleanVersion(GetStr(ql, "version")), desc,
                            ExtractIcon(zip, iconRel, cacheDir, jarPath));
                }
            }

            var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (toml != null)
            {
                using var s = toml.Open();
                using var reader = new StreamReader(s);
                var text = reader.ReadToEnd();
                var name = TomlValue(text, "displayName");
                if (!string.IsNullOrWhiteSpace(name))
                    return new ModDisplayInfo(name!, CleanVersion(TomlValue(text, "version")),
                        TomlValue(text, "description"), ExtractIcon(zip, TomlValue(text, "logoFile"), cacheDir, jarPath));
            }
        }
        catch (Exception ex) { CrashLog.Write("[mods] reading jar metadata failed", ex); }
        return null;
    }

    private static string? GetStr(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Grabs a single-quoted TOML value (<c>key = "value"</c>). First match wins.</summary>
    private static string? TomlValue(string text, string key)
    {
        var m = Regex.Match(text, key + @"\s*=\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Drops unresolved build placeholders like <c>${version}</c>.</summary>
    private static string? CleanVersion(string? v)
        => string.IsNullOrWhiteSpace(v) || v.Contains("${") ? null : v;

    private static string? ExtractIcon(ZipArchive zip, string? relPath, string cacheDir, string jarPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        try
        {
            var entry = zip.GetEntry(relPath) ?? zip.GetEntry(relPath.TrimStart('/'));
            if (entry == null) return null;

            var ext = Path.GetExtension(relPath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
            Directory.CreateDirectory(cacheDir);
            var dest = Path.Combine(cacheDir, "jar_" + Sanitize(Path.GetFileNameWithoutExtension(jarPath)) + ext);
            if (!File.Exists(dest)) entry.ExtractToFile(dest, overwrite: true); // extract once, reuse after
            return dest;
        }
        catch { return null; }
    }

    // -------- file-name cleanup (last resort) --------

    private static readonly HashSet<string> LoaderWords =
        new(StringComparer.OrdinalIgnoreCase) { "fabric", "forge", "neoforge", "quilt", "mc", "fabricmod", "mod" };

    private static string CleanName(string fileName)
    {
        var name = fileName;
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        var tokens = Regex.Split(name, @"[-_+ .]+").Where(t => t.Length > 0).ToArray();
        var kept = new List<string>();
        foreach (var t in tokens)
        {
            if (Regex.IsMatch(t, @"\d") || LoaderWords.Contains(t)) break;
            kept.Add(t);
        }
        if (kept.Count == 0 && tokens.Length > 0) kept.Add(tokens[0]);

        var joined = string.Join(" ", kept);
        return joined.Length == 0 ? fileName : Capitalize(joined);
    }

    private static string Capitalize(string s)
        => string.Join(" ", s.Split(' ')
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
