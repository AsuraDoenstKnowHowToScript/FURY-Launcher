// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Imports a Modrinth modpack (<c>.mrpack</c>): a ZIP with <c>modrinth.index.json</c>
/// (which lists mod download URLs + hashes and the required Minecraft/loader versions)
/// plus <c>overrides/</c> (configs and extra files). Creates an isolated instance,
/// downloads the mods (verifying SHA-1), and applies the overrides. The loader itself
/// is reinstalled on first launch, like our own <c>.frpack</c>.
/// </summary>
public sealed class MrpackService
{
    private const string IndexEntry = "modrinth.index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly InstanceService _instances;

    public MrpackService(HttpClient http, LauncherPaths paths, InstanceService instances)
    {
        _http = http;
        _paths = paths;
        _instances = instances;
    }

    /// <summary>What a pack declares about itself, readable without installing anything.</summary>
    public sealed record PackInfo(string Name, string McVersion, LoaderType Loader, int FileCount);

    /// <summary>
    /// Reads modrinth.index.json only. Needed before installing into an existing instance, so the
    /// user can be told what the pack will turn that instance into before it happens.
    /// </summary>
    public async Task<PackInfo> ReadInfoAsync(string mrpackPath, CancellationToken ct = default)
    {
        var index = await ReadIndexAsync(mrpackPath, ct).ConfigureAwait(false);
        var (mcVersion, loader) = ResolveTarget(index);
        return new PackInfo(
            string.IsNullOrWhiteSpace(index.Name) ? "Modrinth pack" : index.Name!,
            mcVersion, loader, ClientFiles(index).Count);
    }

    /// <summary>Creates a new instance from a <c>.mrpack</c>, downloading its files.</summary>
    public async Task<Instance> ImportAsync(
        string mrpackPath, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var index = await ReadIndexAsync(mrpackPath, ct).ConfigureAwait(false);
        var (mcVersion, loader) = ResolveTarget(index);

        var name = string.IsNullOrWhiteSpace(index.Name) ? "Modrinth pack" : index.Name;
        var instance = await _instances.CreateAsync(name, mcVersion, loader, ct).ConfigureAwait(false);
        await InstallAsync(instance, mrpackPath, index, progress, ct).ConfigureAwait(false);
        return instance;
    }

    /// <summary>
    /// Installs a pack into an instance that already exists, switching that instance to the
    /// Minecraft version and loader the pack needs. Separate from <see cref="ImportAsync"/> so the
    /// choice between a fresh instance and an existing one belongs to the caller, not to us.
    /// </summary>
    public async Task<Instance> InstallIntoAsync(
        Instance instance, string mrpackPath,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var index = await ReadIndexAsync(mrpackPath, ct).ConfigureAwait(false);
        var (mcVersion, loader) = ResolveTarget(index);

        // A pack is a Minecraft version, a loader and a mod list together; installing the mods
        // into an instance still running something else would produce a launch that fails on the
        // first mod. Pointing the instance at the pack's target is what makes it actually run.
        if (!string.Equals(instance.McVersion, mcVersion, StringComparison.OrdinalIgnoreCase) ||
            instance.Loader != loader)
        {
            instance.McVersion = mcVersion;
            instance.Loader = loader;
            instance.LoaderVersion = null; // reinstalled on next launch for the new loader
            await _instances.UpdateAsync(instance, ct).ConfigureAwait(false);
        }

        await InstallAsync(instance, mrpackPath, index, progress, ct).ConfigureAwait(false);
        return instance;
    }

    private async Task<MrpackIndex> ReadIndexAsync(string mrpackPath, CancellationToken ct)
    {
        if (!File.Exists(mrpackPath))
            throw new FileNotFoundException("Arquivo .mrpack nao encontrado.", mrpackPath);

        await using var fs = File.OpenRead(mrpackPath);
        using var zipRead = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zipRead.GetEntry(IndexEntry)
            ?? throw new InvalidOperationException("Modpack invalido: modrinth.index.json ausente.");
        await using var es = entry.Open();
        return await JsonSerializer.DeserializeAsync<MrpackIndex>(es, JsonOptions, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Modpack invalido: modrinth.index.json ilegivel.");
    }

    private static (string McVersion, LoaderType Loader) ResolveTarget(MrpackIndex index)
    {
        var deps = index.Dependencies ?? new Dictionary<string, string>();
        if (!deps.TryGetValue("minecraft", out var mcVersion) || string.IsNullOrWhiteSpace(mcVersion))
            throw new InvalidOperationException("Modpack nao informa a versao do Minecraft.");
        return (mcVersion, ResolveLoader(deps));
    }

    /// <summary>Files to install: anything the pack does not mark as server-only.</summary>
    private static List<MrpackFile> ClientFiles(MrpackIndex index) =>
        (index.Files ?? new List<MrpackFile>())
            .Where(f => f.Env?.Client is not "unsupported")
            .Where(f => f.Downloads is { Count: > 0 } && !string.IsNullOrWhiteSpace(f.Path))
            .ToList();

    private async Task InstallAsync(
        Instance instance, string mrpackPath, MrpackIndex index,
        IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var mcDir = _paths.InstanceMinecraft(instance);
        Directory.CreateDirectory(mcDir);

        var files = ClientFiles(index);
        var total = files.Count;
        var done = 0;
        progress?.Report((done, total));

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = SafeCombine(mcDir, f.Path!);
            var url = f.Downloads!.First(u => !string.IsNullOrWhiteSpace(u));
            f.Hashes.TryGetValue("sha1", out var sha1);
            await DownloadAsync(url, dest, sha1, ct).ConfigureAwait(false);
            progress?.Report((++done, total));
        }

        // Overrides (and client-overrides) are copied verbatim into .minecraft.
        await using (var fs = File.OpenRead(mrpackPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory

                string? rel = null;
                if (entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    rel = entry.FullName.Substring("overrides/".Length);
                else if (entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                    rel = entry.FullName.Substring("client-overrides/".Length);
                if (string.IsNullOrEmpty(rel)) continue;

                var dest = SafeCombine(mcDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var src = entry.Open();
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
        }
    }

    private static LoaderType ResolveLoader(IDictionary<string, string> deps)
    {
        if (deps.ContainsKey("neoforge")) return LoaderType.NeoForge;
        if (deps.ContainsKey("forge")) return LoaderType.Forge;
        if (deps.ContainsKey("fabric-loader")) return LoaderType.Fabric;
        if (deps.ContainsKey("quilt-loader")) return LoaderType.Fabric; // closest loader we support
        return LoaderType.Vanilla;
    }

    private async Task DownloadAsync(string url, string dest, string? sha1, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(sha1))
        {
            await using var check = File.OpenRead(dest);
            var hash = Convert.ToHexString(await SHA1.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(hash, sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Falha de integridade (SHA-1) em {Path.GetFileName(dest)}.");
        }
    }

    /// <summary>Combines and rejects paths that would escape the instance (zip-slip guard).</summary>
    private static string SafeCombine(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Entrada de modpack invalida (fora da pasta): {relative}");
        return full;
    }

    // --- modrinth.index.json shape (only the fields we use) ---

    private sealed class MrpackIndex
    {
        public string? Name { get; set; }
        public List<MrpackFile>? Files { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
    }

    private sealed class MrpackFile
    {
        public string? Path { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = new();
        public MrpackEnv? Env { get; set; }
        public List<string>? Downloads { get; set; }
    }

    private sealed class MrpackEnv
    {
        [JsonPropertyName("client")] public string? Client { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
    }
}
