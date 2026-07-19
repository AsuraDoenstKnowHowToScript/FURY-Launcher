// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;

namespace Launcher.Core.Services;

/// <summary>
/// Downloads and installs Eclipse Temurin (Adoptium) JREs so players who lack the
/// right Java major do not have to hunt for one. Installs land in a launcher-managed
/// folder (java/&lt;major&gt;/bin/java.exe) that <see cref="JavaLocator"/> scans.
/// </summary>
public sealed class JavaInstaller
{
    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;

    public JavaInstaller(HttpClient http, LauncherPaths paths)
    {
        _http = http;
        _paths = paths;
    }

    /// <summary>Major versions offered for install (LTS lines Minecraft uses).</summary>
    public static readonly int[] OfferedMajors = { 8, 17, 21 };

    /// <summary>The java.exe of a launcher-installed JRE for this major, or null.</summary>
    public string? InstalledPath(int major)
    {
        var exe = Path.Combine(_paths.JavaMajorDir(major), "bin", "java.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Downloads the latest GA Temurin JRE (Windows x64) for <paramref name="major"/> and installs it,
    /// returning the java.exe path. If already installed, returns it without downloading.
    /// </summary>
    public async Task<string> InstallAsync(int major, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var existing = InstalledPath(major);
        if (existing != null) return existing;

        // Adoptium's "latest binary" endpoint redirects to the concrete asset (a .zip).
        var url = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
        Directory.CreateDirectory(_paths.JavaDir);
        var tmpZip = Path.Combine(_paths.JavaDir, $"temurin-{major}.tmp.zip");
        var extractTmp = Path.Combine(_paths.JavaDir, $"temurin-{major}.tmp");

        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tmpZip);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            // Extract, then flatten the single top-level folder into java/<major>.
            if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, true);
            ZipFile.ExtractToDirectory(tmpZip, extractTmp);

            var top = Directory.EnumerateDirectories(extractTmp).FirstOrDefault() ?? extractTmp;
            var target = _paths.JavaMajorDir(major);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(top, target);

            var exe = Path.Combine(target, "bin", "java.exe");
            if (!File.Exists(exe))
                throw new InvalidOperationException("The downloaded JRE did not contain bin/java.exe.");
            return exe;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* best effort */ }
            try { if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, true); } catch { /* best effort */ }
        }
    }
}
