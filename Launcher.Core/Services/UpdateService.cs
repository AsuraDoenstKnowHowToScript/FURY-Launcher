// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Core.Services;

/// <summary>A release that is newer than the running build.</summary>
public sealed class UpdateInfo
{
    public required string Version { get; init; }   // tag without the leading "v" (e.g. "0.4.0" or "0.4.0-beta")
    public required string Tag { get; init; }        // raw tag (e.g. "v0.4.0-beta")
    public bool IsBeta { get; init; }                // prerelease flag OR a pre-release version suffix
    public string? ZipUrl { get; init; }             // Windows x64 zip asset download URL
    public string HtmlUrl { get; init; } = "";       // release page
}

/// <summary>
/// Checks the project's GitHub Releases for a newer build and downloads it. Stable
/// releases (no pre-release suffix) are preferred; betas are surfaced too so the UI
/// can warn before installing one. No UI here; the app drives the install/restart.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;

    public UpdateService(HttpClient http) => _http = http;

    /// <summary>
    /// Returns the newest release newer than <paramref name="currentVersion"/>, or null.
    /// When <paramref name="includeBeta"/> is false, pre-releases are ignored.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(
        string owner, string repo, string currentVersion, bool includeBeta, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30");
        req.Headers.UserAgent.ParseAdd($"{repo}-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        UpdateInfo? best = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (GetBool(rel, "draft")) continue;

            var tag = GetString(rel, "tag_name");
            if (string.IsNullOrEmpty(tag)) continue;
            var version = tag.TrimStart('v', 'V');

            var isBeta = GetBool(rel, "prerelease") || version.Contains('-');
            if (isBeta && !includeBeta) continue;
            if (Compare(version, currentVersion) <= 0) continue; // not newer than what we run

            string? zip = null;
            if (rel.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = GetString(a, "name");
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("win", StringComparison.OrdinalIgnoreCase))
                    {
                        zip = GetString(a, "browser_download_url");
                        break;
                    }
                }
            }

            var candidate = new UpdateInfo
            {
                Version = version,
                Tag = tag,
                IsBeta = isBeta,
                ZipUrl = zip,
                HtmlUrl = GetString(rel, "html_url")
            };
            if (best == null || Compare(candidate.Version, best.Version) > 0)
                best = candidate;
        }
        return best;
    }

    /// <summary>
    /// Downloads the release zip and extracts it to a staging folder, returning that path.
    /// The staging folder's root contains the new app files (ready to copy over the install).
    /// </summary>
    public async Task<string> DownloadAndExtractAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.ZipUrl))
            throw new InvalidOperationException("This release has no downloadable Windows build.");

        var root = Path.Combine(Path.GetTempPath(), "FURYLauncherUpdate");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"update-{Sanitize(info.Tag)}.zip");

        using (var resp = await _http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long readTotal = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                readTotal += n;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }

        var staging = Path.Combine(root, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging);

        try { File.Delete(zipPath); } catch { /* best effort */ }
        return staging;
    }

    // --- version comparison (numeric core, then "no pre-release" > "has pre-release") ---

    private const int CoreParts = 4; // major.minor.patch.build (supports x.y.z and x.y.z.w)

    private static int Compare(string a, string b)
    {
        var (ca, pa) = SplitVersion(a);
        var (cb, pb) = SplitVersion(b);
        for (var i = 0; i < CoreParts; i++)
        {
            var r = ca[i].CompareTo(cb[i]);
            if (r != 0) return r;
        }
        if (pa.Length == 0 && pb.Length != 0) return 1;   // stable beats a pre-release of the same core
        if (pa.Length != 0 && pb.Length == 0) return -1;
        return string.CompareOrdinal(pa, pb);
    }

    private static (int[] core, string pre) SplitVersion(string v)
    {
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : "";
        var parts = core.Split('.');
        var nums = new int[CoreParts];
        for (var i = 0; i < CoreParts && i < parts.Length; i++)
            int.TryParse(parts[i], out nums[i]);
        return (nums, pre);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
