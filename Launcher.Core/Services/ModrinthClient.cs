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

    /// <summary>Page size for search; the UI pages by requesting successive <paramref name="offset"/>s.</summary>
    public const int SearchPageSize = 30;

    public async Task<IReadOnlyList<ModrinthHit>> SearchAsync(
        string query, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod,
        int offset = 0, CancellationToken ct = default)
    {
        var facets = BuildFacets(mcVersion, loader, kind);
        var url = $"{BaseUrl}/search?limit={SearchPageSize}&offset={offset}" +
                  $"&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}";

        await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        var response = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        return response?.Hits ?? new List<ModrinthHit>();
    }

    /// <summary>All project versions compatible with the MC version (and loader, for mods). Newest first.</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        var versions = $"[\"{mcVersion}\"]";
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?game_versions={Uri.EscapeDataString(versions)}";
        // Only mods pin the instance loader; shaders/datapacks use their own loader tags.
        if (kind == ContentKind.Mod)
            url += $"&loaders={Uri.EscapeDataString($"[\"{LoaderName(loader)}\"]")}";

        await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<ModrinthVersion>>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        return (list ?? new List<ModrinthVersion>()).Where(v => v.Files.Count > 0).ToList();
    }

    /// <summary>Resolves the newest project version compatible with the MC version (and loader, for mods).</summary>
    public async Task<ModrinthVersion?> GetCompatibleVersionAsync(
        string projectId, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
        => (await GetVersionsAsync(projectId, mcVersion, loader, kind, ct).ConfigureAwait(false)).FirstOrDefault();

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

    /// <summary>
    /// Resolves a jar to its Modrinth version by SHA-512 hash, so a mod added by hand or
    /// from CurseForge can still be identified. Null if the file is not on Modrinth.
    /// </summary>
    public async Task<ModrinthVersion?> GetVersionByHashAsync(string sha512, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/version_file/{Uri.EscapeDataString(sha512)}?algorithm=sha512";
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<ModrinthVersion>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // 404 = not indexed on Modrinth
        }
    }

    /// <summary>Fetches project-level details (title, icon, description) for a mod. Null if unavailable.</summary>
    public async Task<ModrinthProject?> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}";
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<ModrinthProject>(stream, JsonStore.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // project gone / not accessible
        }
    }

    /// <summary>Downloads any URL to a local file (used to cache mod icons). Best-effort.</summary>
    public async Task DownloadFileAsync(string url, string destPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var src = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a version's file into <paramref name="targetDir"/>. Returns the saved path.</summary>
    public async Task<string> DownloadAsync(ModrinthVersion version, string targetDir, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        var file = SelectDownloadFile(version, kind)
            ?? throw new InvalidOperationException("Selected Modrinth version has no downloadable file.");

        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, file.Filename);

        await using var src = await _http.GetStreamAsync(file.Url, ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return dest;
    }

    /// <summary>
    /// Picks the file to download. Datapacks/shaders ship as .zip; a project that is both
    /// a mod and a datapack can mark its .jar primary, so prefer a .zip for those kinds
    /// (otherwise the .jar lands in the folder and never shows in the datapack list).
    /// </summary>
    private static ModrinthFile? SelectDownloadFile(ModrinthVersion version, ContentKind kind)
    {
        if (kind != ContentKind.Mod)
        {
            static bool IsZip(ModrinthFile f) => f.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var zip = version.Files.FirstOrDefault(f => f.Primary && IsZip(f))
                   ?? version.Files.FirstOrDefault(IsZip);
            if (zip != null) return zip;
        }
        return version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
    }

    private static string BuildFacets(string mcVersion, LoaderType loader, ContentKind kind)
    {
        var parts = new List<string>
        {
            $"[\"project_type:{ProjectType(kind)}\"]",
            $"[\"versions:{mcVersion}\"]"
        };
        if (kind == ContentKind.Mod)
            parts.Add($"[\"categories:{LoaderName(loader)}\"]");
        return "[" + string.Join(",", parts) + "]";
    }

    private static string ProjectType(ContentKind kind) => kind switch
    {
        ContentKind.Shader => "shader",
        ContentKind.Datapack => "datapack",
        _ => "mod"
    };

    private static string LoaderName(LoaderType loader) => loader switch
    {
        LoaderType.Fabric => "fabric",
        LoaderType.Forge => "forge",
        LoaderType.NeoForge => "neoforge",
        _ => throw new InvalidOperationException("Vanilla instances do not support mods.")
    };

    private sealed record SearchResponse(List<ModrinthHit> Hits);
}
