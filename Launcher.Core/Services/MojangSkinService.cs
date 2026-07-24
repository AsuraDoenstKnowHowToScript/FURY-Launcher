// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text;
using System.Text.Json;

namespace Launcher.Core.Services;

/// <summary>Resolved Microsoft skin: the on-disk PNG and whether the model is slim (Alex).</summary>
public sealed record MsSkin(string PngPath, bool Slim);

/// <summary>
/// Read-only fetch of a Microsoft account's public skin from the Mojang session server,
/// keyed by uuid and cached on disk (24 h TTL). Pure <see cref="HttpClient"/>, no SDK.
/// Designed to never block the UI and never throw upward: on any failure it falls back to
/// stale cache, and failing that returns null so the caller can show a default avatar.
/// </summary>
public sealed class MojangSkinService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;

    public MojangSkinService(HttpClient http, LauncherPaths paths)
    {
        _http = http;
        _paths = paths;
    }

    /// <summary>
    /// Returns the account's skin (cached PNG + model), refreshing from the network when the
    /// cache is stale. Returns null only when there is neither a live fetch nor any cache.
    /// </summary>
    public async Task<MsSkin?> GetAsync(string uuid, CancellationToken ct = default)
    {
        var id = (uuid ?? "").Replace("-", "").Trim().ToLowerInvariant();
        if (id.Length == 0) return null;

        Directory.CreateDirectory(_paths.MsSkinCacheDir);
        var pngPath = Path.Combine(_paths.MsSkinCacheDir, id + ".png");
        var metaPath = Path.Combine(_paths.MsSkinCacheDir, id + ".meta.json");

        var meta = await ReadMetaAsync(metaPath, ct).ConfigureAwait(false);
        var fresh = meta != null && File.Exists(pngPath) && DateTime.UtcNow - meta.FetchedUtc < Ttl;
        if (fresh) return new MsSkin(pngPath, meta!.Slim);

        try
        {
            var (skinUrl, slim) = await FetchTextureAsync(id, ct).ConfigureAwait(false);
            if (skinUrl != null)
            {
                var bytes = await _http.GetByteArrayAsync(skinUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(pngPath, bytes, ct).ConfigureAwait(false);
                await WriteMetaAsync(metaPath, new CacheMeta { Slim = slim, FetchedUtc = DateTime.UtcNow }, ct)
                    .ConfigureAwait(false);
                return new MsSkin(pngPath, slim);
            }
        }
        catch (Exception ex)
        {
            // Offline / rate-limited / malformed: fall through to stale cache.
            CrashLog.Write($"[msskin] fetch for '{id}' failed", ex);
        }

        // Stale cache is better than nothing; otherwise the caller draws the default avatar.
        if (File.Exists(pngPath)) return new MsSkin(pngPath, meta?.Slim ?? false);
        return null;
    }

    /// <summary>Hits the session server and pulls the skin URL + model out of the textures blob.</summary>
    private async Task<(string? skinUrl, bool slim)> FetchTextureAsync(string id, CancellationToken ct)
    {
        var url = $"https://sessionserver.mojang.com/session/minecraft/profile/{id}";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));

        if (!doc.RootElement.TryGetProperty("properties", out var props)) return (null, false);
        foreach (var p in props.EnumerateArray())
        {
            if (!p.TryGetProperty("name", out var n) || n.GetString() != "textures") continue;
            if (!p.TryGetProperty("value", out var v) || v.GetString() is not { } b64) continue;

            using var tex = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            if (!tex.RootElement.TryGetProperty("textures", out var textures)) return (null, false);
            if (!textures.TryGetProperty("SKIN", out var skin)) return (null, false);

            var skinUrl = skin.TryGetProperty("url", out var u) ? u.GetString() : null;
            var slim = skin.TryGetProperty("metadata", out var md)
                       && md.TryGetProperty("model", out var m)
                       && string.Equals(m.GetString(), "slim", StringComparison.OrdinalIgnoreCase);
            return (skinUrl, slim);
        }
        return (null, false);
    }

    private static async Task<CacheMeta?> ReadMetaAsync(string path, CancellationToken ct)
    {
        try { return await JsonStore.ReadAsync<CacheMeta>(path, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static async Task WriteMetaAsync(string path, CacheMeta meta, CancellationToken ct)
    {
        try { await JsonStore.WriteAsync(path, meta, ct).ConfigureAwait(false); } catch { /* cache is best-effort */ }
    }

    private sealed class CacheMeta
    {
        public bool Slim { get; set; }
        public DateTime FetchedUtc { get; set; }
    }
}
