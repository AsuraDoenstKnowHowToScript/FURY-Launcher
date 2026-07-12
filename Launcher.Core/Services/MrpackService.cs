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

    /// <summary>Creates a new instance from a <c>.mrpack</c>, downloading its files.</summary>
    public async Task<Instance> ImportAsync(
        string mrpackPath, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath))
            throw new FileNotFoundException("Arquivo .mrpack nao encontrado.", mrpackPath);

        MrpackIndex index;
        await using (var fs = File.OpenRead(mrpackPath))
        using (var zipRead = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zipRead.GetEntry(IndexEntry)
                ?? throw new InvalidOperationException("Modpack invalido: modrinth.index.json ausente.");
            await using var es = entry.Open();
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(es, JsonOptions, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Modpack invalido: modrinth.index.json ilegivel.");
        }

        var deps = index.Dependencies ?? new Dictionary<string, string>();
        if (!deps.TryGetValue("minecraft", out var mcVersion) || string.IsNullOrWhiteSpace(mcVersion))
            throw new InvalidOperationException("Modpack nao informa a versao do Minecraft.");
        var loader = ResolveLoader(deps);

        var name = string.IsNullOrWhiteSpace(index.Name) ? "Modrinth pack" : index.Name;
        var instance = await _instances.CreateAsync(name, mcVersion, loader, ct).ConfigureAwait(false);
        var mcDir = _paths.InstanceMinecraft(instance);
        Directory.CreateDirectory(mcDir);

        // Files to install (skip anything explicitly unsupported on the client).
        var files = (index.Files ?? new List<MrpackFile>())
            .Where(f => f.Env?.Client is not "unsupported")
            .Where(f => f.Downloads is { Count: > 0 } && !string.IsNullOrWhiteSpace(f.Path))
            .ToList();

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

        return instance;
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
