// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Net.Http.Headers;
using System.Text.Json;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Thin client over the public Modrinth API (v2). Search returns projects; the
/// version lookup filters by the instance's MC version + loader so only
/// compatible files are offered.
/// </summary>
public sealed class ModrinthClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http)
    {
        _http = http;
        // Modrinth asks every client to send a descriptive User-Agent.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                AppInfo.Name.Replace(" ", ""), AppInfo.Version));
    }

    public async Task<IReadOnlyList<ModrinthHit>> SearchAsync(
        string query, string mcVersion, LoaderType loader, CancellationToken ct = default)
    {
        var facets = $"[[\"project_type:mod\"],[\"versions:{mcVersion}\"],[\"categories:{LoaderName(loader)}\"]]";
        var url = $"{BaseUrl}/search?limit=30&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}";

        await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        var response = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        return response?.Hits ?? new List<ModrinthHit>();
    }

    /// <summary>All project versions compatible with the MC version + loader (newest first).</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId, string mcVersion, LoaderType loader, CancellationToken ct = default)
    {
        var loaders = $"[\"{LoaderName(loader)}\"]";
        var versions = $"[\"{mcVersion}\"]";
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?loaders={Uri.EscapeDataString(loaders)}&game_versions={Uri.EscapeDataString(versions)}";

        await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<ModrinthVersion>>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        return (list ?? new List<ModrinthVersion>()).Where(v => v.Files.Count > 0).ToList();
    }

    /// <summary>Resolves the newest project version compatible with the MC version + loader.</summary>
    public async Task<ModrinthVersion?> GetCompatibleVersionAsync(
        string projectId, string mcVersion, LoaderType loader, CancellationToken ct = default)
        => (await GetVersionsAsync(projectId, mcVersion, loader, ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Fetches a specific version by its id (used to satisfy pinned dependencies).</summary>
    public async Task<ModrinthVersion?> GetVersionByIdAsync(string versionId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}";
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<ModrinthVersion>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // version gone / not accessible
        }
    }

    /// <summary>Downloads a version's primary file into <paramref name="targetDir"/>. Returns the saved path.</summary>
    public async Task<string> DownloadAsync(ModrinthVersion version, string targetDir, CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
            ?? throw new InvalidOperationException("Selected Modrinth version has no downloadable file.");

        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, file.Filename);

        await using var src = await _http.GetStreamAsync(file.Url, ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return dest;
    }

    private static string LoaderName(LoaderType loader) => loader switch
    {
        LoaderType.Fabric => "fabric",
        LoaderType.Forge => "forge",
        LoaderType.NeoForge => "neoforge",
        _ => throw new InvalidOperationException("Vanilla instances do not support mods.")
    };

    private sealed record SearchResponse(List<ModrinthHit> Hits);
}
