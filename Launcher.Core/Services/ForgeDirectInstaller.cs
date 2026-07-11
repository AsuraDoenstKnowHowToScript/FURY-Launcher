// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Launcher.Core.Services;

/// <summary>
/// Installs Forge / NeoForge by downloading the official installer jar
/// <b>directly from Maven</b> (never the website, never adfoc.us) and running it
/// headless in client-install mode. Returns the resulting launch version id
/// (the new folder under <c>versions/</c>).
/// </summary>
public sealed class ForgeDirectInstaller
{
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string ForgeMetadata = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string ForgePromotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http;

    public ForgeDirectInstaller(HttpClient http) => _http = http;

    // ------------------------------------------------------------------ Forge

    public async Task<string> InstallForgeAsync(
        string mcVersion, string minecraftDir, string? javaPathOverride,
        IProgress<string>? log, CancellationToken ct = default)
    {
        var artifact = await ResolveForgeArtifactAsync(mcVersion, ct).ConfigureAwait(false);
        var url = $"{ForgeMaven}/{artifact}/forge-{artifact}-installer.jar";
        log?.Report($"[forge] Versao resolvida: {artifact} (direto do Maven, sem adfoc.us)");
        return await DownloadAndRunAsync(url, "forge", mcVersion, minecraftDir, javaPathOverride, log, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the exact Maven artifact folder name for the recommended (else
    /// latest) Forge build. The promotions feed gives the build number, but old
    /// versions carry a branch suffix in the Maven path (e.g.
    /// <c>1.8.9-11.15.1.2318-1.8.9</c>), so we reconcile it against maven-metadata.
    /// </summary>
    private async Task<string> ResolveForgeArtifactAsync(string mcVersion, CancellationToken ct)
    {
        // 1) The promoted build number (e.g. "47.2.0" or "11.15.1.2318").
        string? promo = null;
        try
        {
            await using var stream = await _http.GetStreamAsync(ForgePromotions, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var promos = doc.RootElement.GetProperty("promos");
            if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec)) promo = rec.GetString();
            else if (promos.TryGetProperty($"{mcVersion}-latest", out var latest)) promo = latest.GetString();
        }
        catch { /* fall back to metadata-only resolution below */ }

        // 2) All real artifact folder names from maven-metadata.
        var all = await LoadForgeVersionsAsync(ct).ConfigureAwait(false);

        if (promo != null)
        {
            var exact = $"{mcVersion}-{promo}";
            if (all.Contains(exact)) return exact;                              // modern: exact match
            var branch = all.FirstOrDefault(v => v.StartsWith(exact + "-", StringComparison.Ordinal));
            if (branch != null) return branch;                                  // legacy: branch-suffixed
        }

        // 3) Fallback: highest artifact for this MC version.
        var forThisMc = all.Where(v => v.StartsWith(mcVersion + "-", StringComparison.Ordinal))
            .OrderBy(v => v, new DottedVersionComparer()).ToList();
        if (forThisMc.Count > 0) return forThisMc[^1];

        throw new InvalidOperationException(
            $"Nenhuma versao do Forge encontrada para o Minecraft {mcVersion}.");
    }

    private async Task<HashSet<string>> LoadForgeVersionsAsync(CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(ForgeMetadata, ct).ConfigureAwait(false);
        var xml = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        return xml.Descendants("version").Select(v => v.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    // --------------------------------------------------------------- NeoForge

    public async Task<string> InstallNeoForgeAsync(
        string mcVersion, string minecraftDir, string? javaPathOverride,
        IProgress<string>? log, CancellationToken ct = default)
    {
        var neoVer = await ResolveNeoForgeVersionAsync(mcVersion, ct).ConfigureAwait(false);
        var url = $"{NeoForgeMaven}/{neoVer}/neoforge-{neoVer}-installer.jar";
        log?.Report($"[neoforge] Versao resolvida: {neoVer} (direto do Maven)");
        return await DownloadAndRunAsync(url, "neoforge", mcVersion, minecraftDir, javaPathOverride, log, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// NeoForge encodes the MC version in its own version (MC 1.20.4 → 20.4.x,
    /// MC 1.21 → 21.0.x). Picks the highest build matching that prefix.
    /// </summary>
    private async Task<string> ResolveNeoForgeVersionAsync(string mcVersion, CancellationToken ct)
    {
        var m = Regex.Match(mcVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (!m.Success)
            throw new InvalidOperationException($"Versao do Minecraft invalida para NeoForge: {mcVersion}");
        int minor = int.Parse(m.Groups[1].Value);
        int patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var prefix = $"{minor}.{patch}.";

        await using var stream = await _http.GetStreamAsync($"{NeoForgeMaven}/maven-metadata.xml", ct).ConfigureAwait(false);
        var xml = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        var versions = xml.Descendants("version").Select(v => v.Value).ToList();

        var match = versions
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(v => v, new DottedVersionComparer())
            .LastOrDefault();

        if (match == null)
            throw new InvalidOperationException(
                $"Nenhuma versao do NeoForge encontrada para o Minecraft {mcVersion} (prefixo {prefix}).");
        return match;
    }

    private sealed class DottedVersionComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            int[] Parse(string? s) => (s ?? "").Split('.', '-')
                .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pa = Parse(a); var pb = Parse(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int va = i < pa.Length ? pa[i] : 0, vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }
    }

    // ------------------------------------------------------------ Shared work

    private async Task<string> DownloadAndRunAsync(
        string installerUrl, string loaderKeyword, string mcVersion, string minecraftDir,
        string? javaPathOverride, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(minecraftDir);
        EnsureLauncherProfiles(minecraftDir);

        // 1) Download the installer jar directly from Maven.
        var jarPath = Path.Combine(Path.GetTempPath(), $"fury-{loaderKeyword}-{Guid.NewGuid():N}.jar");
        log?.Report($"[loader] Baixando instalador: {installerUrl}");
        await DownloadFileAsync(installerUrl, jarPath, ct).ConfigureAwait(false);

        try
        {
            var versionsDir = Path.Combine(minecraftDir, "versions");

            // Legacy installers (Forge <= 1.12.2) embed the version json + universal
            // jar and DON'T support --installClient. Detect that and extract manually;
            // modern installers (Forge 1.13+, all NeoForge) run headless with processors.
            if (TryReadLegacyProfile(jarPath, out var versionInfo, out var installNode))
            {
                log?.Report("[loader] Instalador legacy detectado — extraindo manualmente (sem processos).");
                var legacyId = ExtractLegacyForge(jarPath, minecraftDir, versionInfo, installNode);
                log?.Report($"[loader] Instalado: {legacyId}");
                return legacyId;
            }

            var before = SnapshotVersions(versionsDir);

            var java = JavaLocator.Resolve(mcVersion, javaPathOverride);
            log?.Report($"[loader] Java: {java}");
            log?.Report("[loader] Instalando (headless, --installClient)...");
            await RunInstallerAsync(java, jarPath, minecraftDir, log, ct).ConfigureAwait(false);

            var after = SnapshotVersions(versionsDir);
            var created = after.Except(before)
                .Where(n => n.Contains(loaderKeyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string versionId = created.FirstOrDefault()
                // Already installed before? Reuse the existing matching folder.
                ?? after.FirstOrDefault(n => n.Contains(loaderKeyword, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "Instalador do loader terminou mas nenhuma versao foi criada em versions/.");

            log?.Report($"[loader] Instalado: {versionId}");
            return versionId;
        }
        finally
        {
            try { File.Delete(jarPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    // ------------------------------------------------------- Legacy extraction

    /// <summary>
    /// Reads <c>install_profile.json</c> from the installer jar. Legacy Forge
    /// (&lt;= 1.12.2) has an <c>install</c> + <c>versionInfo</c> shape; modern
    /// installers don't, so this returns false for them.
    /// </summary>
    private static bool TryReadLegacyProfile(string jarPath, out JsonElement versionInfo, out JsonElement installNode)
    {
        versionInfo = default;
        installNode = default;
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = zip.GetEntry("install_profile.json");
        if (entry == null) return false;

        using var s = entry.Open();
        using var doc = JsonDocument.Parse(ReadAll(s));
        if (!doc.RootElement.TryGetProperty("versionInfo", out var vi) ||
            !doc.RootElement.TryGetProperty("install", out var inst))
            return false;

        // Clone out of the disposed document.
        versionInfo = vi.Clone();
        installNode = inst.Clone();
        return true;
    }

    private static string ExtractLegacyForge(string jarPath, string minecraftDir, JsonElement versionInfo, JsonElement installNode)
    {
        var id = versionInfo.GetProperty("id").GetString()
                 ?? throw new InvalidOperationException("install_profile.json legacy sem id.");

        // 1) Write versions/<id>/<id>.json from the embedded versionInfo.
        var versionDir = Path.Combine(minecraftDir, "versions", id);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, id + ".json"), versionInfo.GetRawText());

        // 2) Copy the embedded universal jar into libraries/ at its Maven path.
        var mavenCoords = installNode.GetProperty("path").GetString()
                          ?? throw new InvalidOperationException("install_profile.json legacy sem install.path.");
        var innerFile = installNode.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;

        var libRelative = MavenCoordsToPath(mavenCoords);
        var libDest = Path.Combine(minecraftDir, "libraries", libRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(libDest)!);

        using (var zip = ZipFile.OpenRead(jarPath))
        {
            var entry = (innerFile != null ? zip.GetEntry(innerFile) : null)
                        ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith("-universal.jar", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Jar universal do Forge nao encontrado no instalador legacy.");
            entry.ExtractToFile(libDest, overwrite: true);
        }

        return id;
    }

    /// <summary>net.minecraftforge:forge:VER → net/minecraftforge/forge/VER/forge-VER.jar</summary>
    private static string MavenCoordsToPath(string coords)
    {
        var parts = coords.Split(':');
        if (parts.Length < 3)
            throw new InvalidOperationException($"Coordenadas Maven invalidas: {coords}");
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var jarName = $"{artifact}-{version}.jar";
        return Path.Combine(group, artifact, version, jarName);
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static async Task RunInstallerAsync(
        string javaPath, string jarPath, string minecraftDir, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = minecraftDir
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(minecraftDir);

        // Legacy installers ignore the path arg and install into %APPDATA%/.minecraft.
        // Our per-instance minecraft dir is literally named ".minecraft", so pointing
        // APPDATA at its parent makes legacy installs land in the right isolated folder.
        var parent = Path.GetDirectoryName(minecraftDir.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
            psi.Environment["APPDATA"] = parent;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report("  " + e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Report("  " + e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException("Falha ao iniciar o processo do Java para o instalador.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Instalador do loader falhou (codigo {proc.ExitCode}). Veja o log acima.");
    }

    private static HashSet<string> SnapshotVersions(string versionsDir)
    {
        if (!Directory.Exists(versionsDir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    /// <summary>The Forge/NeoForge installer requires a launcher_profiles.json to exist.</summary>
    private static void EnsureLauncherProfiles(string minecraftDir)
    {
        var file = Path.Combine(minecraftDir, "launcher_profiles.json");
        if (File.Exists(file)) return;
        Directory.CreateDirectory(minecraftDir);
        const string minimal =
            "{\"profiles\":{},\"selectedProfileName\":\"\",\"clientToken\":\"\"," +
            "\"authenticationDatabase\":{},\"launcherVersion\":{\"name\":\"\",\"format\":21}}";
        File.WriteAllText(file, minimal);
    }
}
