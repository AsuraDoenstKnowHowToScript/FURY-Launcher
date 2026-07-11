// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.Json;

namespace Launcher.Core.Services;

/// <summary>
/// Lists available Minecraft versions from Mojang's public version manifest, so the
/// UI can offer a dropdown instead of a free-text box. Releases only by default;
/// the result is cached for the process lifetime. No UI, network read only.
/// </summary>
public sealed class VersionListService
{
    private const string ManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpClient _http;
    private IReadOnlyList<string>? _releases;

    public VersionListService(HttpClient http) => _http = http;

    /// <summary>Release version ids, newest first (as Mojang orders them). Cached after the first call.</summary>
    public async Task<IReadOnlyList<string>> GetReleasesAsync(CancellationToken ct = default)
    {
        if (_releases != null) return _releases;

        using var resp = await _http.GetAsync(ManifestUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var list = new List<string>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.TryGetProperty("type", out var type) && type.GetString() == "release" &&
                v.TryGetProperty("id", out var id) && id.GetString() is { } vid)
            {
                list.Add(vid);
            }
        }

        _releases = list;
        return list;
    }
}
