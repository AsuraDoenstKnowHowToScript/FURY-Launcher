// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.RegularExpressions;

namespace Launcher.Core.Services;

/// <summary>
/// Finds a Java executable on the machine to run the Forge/NeoForge installer
/// headless. Picks a JDK whose major version fits the Minecraft version (old MC
/// → Java 8, 1.17–1.20.4 → Java 17, newer → Java 21+), falling back to whatever
/// is on PATH. No UI, no network.
/// </summary>
public static class JavaLocator
{
    /// <summary>
    /// Resolves a java executable path. Honors <paramref name="overridePath"/>
    /// first, then tries to match the Minecraft version, then PATH/JAVA_HOME.
    /// </summary>
    /// <exception cref="InvalidOperationException">No Java could be found.</exception>
    public static string Resolve(string mcVersion, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath!;

        var desired = DesiredMajor(mcVersion);
        var found = Discover().ToList();

        if (found.Count > 0)
        {
            // Exact major, else the smallest major >= desired, else the largest.
            var exact = found.FirstOrDefault(j => j.Major == desired);
            if (exact.Path != null) return exact.Path;

            var atLeast = found.Where(j => j.Major >= desired).OrderBy(j => j.Major).FirstOrDefault();
            if (atLeast.Path != null) return atLeast.Path;

            return found.OrderByDescending(j => j.Major).First().Path;
        }

        // Last resort: bare "java" on PATH (major unknown).
        var onPath = FromEnv();
        if (onPath != null) return onPath;

        throw new InvalidOperationException(
            "Nenhum Java encontrado para rodar o instalador do loader. Instale um JDK " +
            "(ex.: Adoptium/Temurin) ou defina o caminho do Java na instância.");
    }

    private static int DesiredMajor(string mcVersion)
    {
        // Parse "1.<minor>(.<patch>)".
        var m = Regex.Match(mcVersion ?? "", @"^1\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return 17; // unknown → a safe modern default
        int minor = int.Parse(m.Groups[1].Value);
        int patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;

        if (minor >= 21) return 21;
        if (minor == 20 && patch >= 5) return 21; // 1.20.5/1.20.6 moved to Java 21
        if (minor >= 17) return 17;
        return 8;
    }

    private readonly record struct Java(string Path, int Major);

    private static IEnumerable<Java> Discover()
    {
        var roots = new List<string>();
        foreach (var pf in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            roots.Add(Path.Combine(pf, "Eclipse Adoptium"));
            roots.Add(Path.Combine(pf, "Java"));
            roots.Add(Path.Combine(pf, "Microsoft"));
            roots.Add(Path.Combine(pf, "Zulu"));
            roots.Add(Path.Combine(pf, "AdoptOpenJDK"));
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (!File.Exists(exe)) exe = Path.Combine(dir, "bin", "java");
                if (!File.Exists(exe)) continue;

                var major = MajorFromName(Path.GetFileName(dir));
                if (major > 0) yield return new Java(exe, major);
            }
        }
    }

    private static int MajorFromName(string folder)
    {
        // e.g. "jdk-17", "jdk-8.0.482.8-hotspot", "jdk-25.0.2.10-hotspot".
        var m = Regex.Match(folder, @"(?:jdk|jre|zulu)[-_]?(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(folder, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static string? FromEnv()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            foreach (var exe in new[] { Path.Combine(home, "bin", "java.exe"), Path.Combine(home, "bin", "java") })
                if (File.Exists(exe)) return exe;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var exe in new[] { Path.Combine(dir, "java.exe"), Path.Combine(dir, "java") })
                    if (File.Exists(exe)) return exe;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
