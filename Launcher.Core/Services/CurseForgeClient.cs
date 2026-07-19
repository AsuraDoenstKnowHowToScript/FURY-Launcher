// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.Json;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Thin client over the CurseForge API (v1). Requires an API key (see
/// <see cref="CurseForgeKey"/>); when absent, <see cref="HasKey"/> is false and the UI
/// disables the source. Results are adapted into the shared Modrinth* records so the
/// browser, version chooser and installer are identical to the Modrinth path.
/// </summary>
public sealed class CurseForgeClient
{
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private readonly HttpClient _http;
    private readonly string _key;

    public CurseForgeClient(HttpClient http, string apiKey)
    {
        _http = http;
        _key = apiKey ?? "";
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_key);

    public const int SearchPageSize = 30;

    // CurseForge "classId" per content type, and "modLoaderType" per loader.
    private static int ClassId(ContentKind kind) => kind switch
    {
        ContentKind.Shader => 6552,
        ContentKind.Datapack => 6945,
        _ => 6 // Mods
    };

    private static int LoaderId(LoaderType loader) => loader switch
    {
        LoaderType.Forge => 1,
        LoaderType.Fabric => 4,
        LoaderType.NeoForge => 6,
        _ => 0 // Any / Vanilla
    };

    private HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", _key);
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var req = Request(url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonStore.Options, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModrinthHit>> SearchAsync(
        string query, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod,
        int offset = 0, CancellationToken ct = default)
    {
        if (!HasKey) return new List<ModrinthHit>();

        var url = $"{BaseUrl}/mods/search?gameId={MinecraftGameId}&classId={ClassId(kind)}" +
                  $"&sortField=2&sortOrder=desc&pageSize={SearchPageSize}&index={offset}" +
                  $"&gameVersion={Uri.EscapeDataString(mcVersion)}" +
                  $"&searchFilter={Uri.EscapeDataString(query)}";
        if (kind == ContentKind.Mod)
            url += $"&modLoaderType={LoaderId(loader)}";

        var resp = await GetAsync<CfSearchResponse>(url, ct).ConfigureAwait(false);
        var hits = new List<ModrinthHit>();
        foreach (var m in resp?.Data ?? new List<CfMod>())
            hits.Add(ToHit(m));
        return hits;
    }

    /// <summary>Files of a mod compatible with the MC version (and loader, for mods), newest first.</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
    {
        if (!HasKey) return new List<ModrinthVersion>();

        var url = $"{BaseUrl}/mods/{Uri.EscapeDataString(projectId)}/files" +
                  $"?gameVersion={Uri.EscapeDataString(mcVersion)}&pageSize=50&index=0";
        if (kind == ContentKind.Mod)
            url += $"&modLoaderType={LoaderId(loader)}";

        var resp = await GetAsync<CfFilesResponse>(url, ct).ConfigureAwait(false);
        return (resp?.Data ?? new List<CfFile>())
            .OrderByDescending(f => f.Id)
            .Select(ToVersion)
            .ToList();
    }

    public async Task<ModrinthVersion?> GetCompatibleVersionAsync(
        string projectId, string mcVersion, LoaderType loader, ContentKind kind = ContentKind.Mod, CancellationToken ct = default)
        => (await GetVersionsAsync(projectId, mcVersion, loader, kind, ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Project details (name/icon/description) to label an installed CurseForge mod.</summary>
    public async Task<ModrinthProject?> GetModAsync(string projectId, CancellationToken ct = default)
    {
        if (!HasKey) return null;
        var resp = await GetAsync<CfModResponse>($"{BaseUrl}/mods/{Uri.EscapeDataString(projectId)}", ct).ConfigureAwait(false);
        var m = resp?.Data;
        if (m == null) return null;
        return new ModrinthProject(m.Id.ToString(), m.Slug, m.Name, m.Summary, m.Logo?.Url);
    }

    private static ModrinthHit ToHit(CfMod m) => new(
        ProjectId: m.Id.ToString(),
        Slug: m.Slug ?? "",
        Title: m.Name ?? "",
        Description: m.Summary ?? "",
        Downloads: m.DownloadCount,
        IconUrl: m.Logo?.Url,
        Author: m.Authors?.FirstOrDefault()?.Name);

    private static ModrinthVersion ToVersion(CfFile f)
    {
        var deps = (f.Dependencies ?? new List<CfDependency>())
            .Where(d => d.RelationType == 3) // required only
            .Select(d => new ModrinthDependency(d.ModId.ToString(), null, "required"))
            .ToList();

        var fileName = f.FileName ?? f.DisplayName ?? $"curseforge-{f.Id}.jar";
        var url = f.DownloadUrl ?? BuildEdgeUrl(f.Id, fileName);
        var files = new List<ModrinthFile> { new(url, fileName, true) };

        return new ModrinthVersion(
            Id: f.Id.ToString(),
            ProjectId: f.ModId.ToString(),
            Name: f.DisplayName ?? fileName,
            VersionNumber: f.DisplayName ?? fileName,
            Files: files,
            Dependencies: deps);
    }

    /// <summary>Reconstructs the public CDN URL for files whose downloadUrl the API omits.</summary>
    private static string BuildEdgeUrl(long fileId, string fileName)
    {
        var id = fileId.ToString();
        var head = id.Length > 4 ? id[..4] : id;
        var tailStr = id.Length > 4 ? id[4..] : "0";
        var tail = int.TryParse(tailStr, out var t) ? t : 0;
        return $"https://edge.forgecdn.net/files/{head}/{tail}/{Uri.EscapeDataString(fileName)}";
    }
}
