// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;
using System.Text.Json;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Exports and imports <c>.frpack</c> (FURY Package) files: a self-contained ZIP
/// holding <c>fury.json</c> (manifest), the real mod jars under <c>mods/</c> and
/// optional instance <c>config/</c>. No UI, no network; a friend imports a file
/// and plays, even offline.
/// </summary>
public sealed class PackService
{
    private const string ManifestEntry = "fury.json";
    private readonly LauncherPaths _paths;
    private readonly InstanceService _instances;

    public PackService(LauncherPaths paths, InstanceService instances)
    {
        _paths = paths;
        _instances = instances;
    }

    // ------------------------------------------------------------------ Export

    /// <summary>Writes a <c>.frpack</c> for the given instance to <paramref name="destPath"/>.</summary>
    public async Task ExportAsync(Instance instance, string destPath, CancellationToken ct = default)
    {
        var modsDir = _paths.InstanceModsDir(instance);
        var configDir = _paths.InstanceConfigDir(instance);

        var manifest = new PackManifest
        {
            Name = instance.Name,
            McVersion = instance.McVersion,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            MinRamMb = instance.MinRamMb,
            MaxRamMb = instance.MaxRamMb,
            JvmArgs = instance.JvmArgs,
            Mods = Directory.Exists(modsDir)
                ? Directory.EnumerateFiles(modsDir).Select(Path.GetFileName).Where(n => n != null).ToList()!
                : new List<string>()
        };

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file then move, so a failure never leaves a half pack.
        var tmp = destPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        await using (var zipStream = File.Create(tmp))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // Manifest
            var manifestEntry = zip.CreateEntry(ManifestEntry);
            await using (var es = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(es, manifest, JsonStore.Options, ct).ConfigureAwait(false);

            // Mods (the real jars)
            if (Directory.Exists(modsDir))
                foreach (var file in Directory.EnumerateFiles(modsDir))
                {
                    ct.ThrowIfCancellationRequested();
                    await AddFileAsync(zip, file, "mods/" + Path.GetFileName(file), ct).ConfigureAwait(false);
                }

            // Optional config tree
            if (Directory.Exists(configDir))
                foreach (var file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(configDir, file).Replace(Path.DirectorySeparatorChar, '/');
                    await AddFileAsync(zip, file, "config/" + rel, ct).ConfigureAwait(false);
                }
        }

        File.Move(tmp, destPath, overwrite: true);
    }

    private static async Task AddFileAsync(ZipArchive zip, string sourcePath, string entryName, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var src = File.OpenRead(sourcePath);
        await using var dst = entry.Open();
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ Read

    /// <summary>Reads the manifest and validates a pack without importing it.</summary>
    public async Task<PackPreview> ReadManifestAsync(string frpackPath, CancellationToken ct = default)
    {
        if (!File.Exists(frpackPath))
            throw new FileNotFoundException("Arquivo .frpack nao encontrado.", frpackPath);

        await using var fs = File.OpenRead(frpackPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidOperationException("Pacote invalido: fury.json ausente.");

        PackManifest manifest;
        await using (var es = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<PackManifest>(es, JsonStore.Options, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Pacote invalido: fury.json ilegivel.");

        var warnings = new List<string>();

        if (manifest.Format > PackManifest.CurrentFormat)
            warnings.Add($"Pacote foi criado num formato mais novo (v{manifest.Format}); pode nao importar 100%.");
        if (string.IsNullOrWhiteSpace(manifest.McVersion))
            warnings.Add("Manifesto sem versao do Minecraft.");
        if (!Enum.IsDefined(manifest.Loader))
            warnings.Add("Loader do pacote desconhecido para este launcher.");
        if (manifest.Loader == LoaderType.Vanilla && manifest.Mods.Count > 0)
            warnings.Add("Pacote traz mods mas o loader e Vanilla; os mods nao vao carregar.");

        // Cross-check declared mods vs. what is actually inside the zip.
        var present = zip.Entries
            .Where(e => e.FullName.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = manifest.Mods.Where(m => !present.Contains(m)).ToList();
        if (missing.Count > 0)
            warnings.Add($"{missing.Count} mod(s) listados no manifesto nao estao empacotados: {string.Join(", ", missing)}");

        return new PackPreview { Manifest = manifest, Warnings = warnings };
    }

    // ------------------------------------------------------------------ Import

    /// <summary>
    /// Creates a new isolated instance from a <c>.frpack</c>, extracting its mods
    /// and config. The loader is reinstalled on first launch (LoaderVersion is not
    /// carried over), so the instance is genuinely playable.
    /// </summary>
    public async Task<Instance> ImportAsync(string frpackPath, CancellationToken ct = default)
    {
        var preview = await ReadManifestAsync(frpackPath, ct).ConfigureAwait(false);
        var m = preview.Manifest;

        var instance = await _instances.CreateAsync(
            string.IsNullOrWhiteSpace(m.Name) ? "Pacote importado" : m.Name,
            m.McVersion, m.Loader, ct).ConfigureAwait(false);

        instance.MinRamMb = m.MinRamMb;
        instance.MaxRamMb = m.MaxRamMb;
        instance.JvmArgs = m.JvmArgs;
        await _instances.UpdateAsync(instance, ct).ConfigureAwait(false);

        var modsDir = _paths.InstanceModsDir(instance);
        var configDir = _paths.InstanceConfigDir(instance);
        Directory.CreateDirectory(modsDir);

        await using var fs = File.OpenRead(frpackPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            string? targetDir = null;
            if (entry.FullName.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                targetDir = modsDir;
            else if (entry.FullName.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
                targetDir = configDir;
            else
                continue; // ignore fury.json and anything else

            var rel = entry.FullName.Substring(entry.FullName.IndexOf('/') + 1);
            var dest = SafeCombine(targetDir!, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            await using var src = entry.Open();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        return instance;
    }

    /// <summary>Combines and rejects paths that would escape the target (zip-slip guard).</summary>
    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Entrada de pacote invalida (fora da pasta): {relative}");
        return full;
    }
}
