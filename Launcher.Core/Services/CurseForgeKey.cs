// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Reflection;

namespace Launcher.Core.Services;

/// <summary>
/// Resolves the CurseForge API key without ever committing it. Order:
///   1. the CURSEFORGE_API_KEY environment variable (devs / power users),
///   2. an embedded <c>curseforge.key</c> resource, written by CI from a secret,
///   3. a local <c>curseforge.key</c> next to the launcher's data.
/// When none is set, <see cref="Value"/> is empty and the CurseForge source is disabled.
/// </summary>
public static class CurseForgeKey
{
    private static string? _cached;
    private static bool _resolved;

    /// <summary>Real CurseForge keys are ~60-char bcrypt strings; anything much shorter is garbage
    /// (e.g. a key mangled by a build script), not a key.</summary>
    public const int MinPlausibleLength = 20;

    public static string Value => _cached ??= Resolve();

    public static bool HasKey => !string.IsNullOrWhiteSpace(Value);

    /// <summary>A key is present and long enough to plausibly be real.</summary>
    public static bool LooksValid => Value.Length >= MinPlausibleLength;

    /// <param name="localFile">Optional path to a per-machine key file (usually under the data root).</param>
    public static string Resolve(string? localFile = null)
    {
        if (_resolved && localFile == null) return _cached ?? "";

        var env = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return Store(env);

        var embedded = ReadEmbedded();
        if (!string.IsNullOrWhiteSpace(embedded)) return Store(embedded);

        if (!string.IsNullOrWhiteSpace(localFile) && File.Exists(localFile))
        {
            try { return Store(File.ReadAllText(localFile)); } catch { /* ignore */ }
        }
        return Store("");
    }

    private static string Store(string v)
    {
        _cached = v.Trim();
        _resolved = true;
        // Fail loud in the right place: a present-but-tiny key is corruption, not a key.
        // Log the LENGTH only — never the value.
        if (_cached.Length > 0 && _cached.Length < MinPlausibleLength)
            CrashLog.Write($"[curseforge] API key looks invalid: {_cached.Length} chars (expected ~60). " +
                           "Check the CURSEFORGE_API_KEY secret / build embedding.");
        return _cached;
    }

    private static string? ReadEmbedded()
    {
        try
        {
            var asm = typeof(CurseForgeKey).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("curseforge.key", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return null; }
    }
}
