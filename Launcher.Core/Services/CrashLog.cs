// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Services;

/// <summary>
/// Appends error/diagnostic lines to a <c>crash.log</c> next to the executable so
/// failures are never silent, even when the user does not have the log panel open.
/// Writing must never throw (logging is best-effort by definition).
/// </summary>
public static class CrashLog
{
    private static string? _path;
    private static readonly object _gate = new();

    /// <summary>Sets the crash.log path (called once at startup by the app).</summary>
    public static void Initialize(string path) => _path = path;

    /// <summary>Appends a timestamped line. No-op if not initialized; never throws.</summary>
    public static void Write(string message)
    {
        var path = _path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            lock (_gate)
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    /// <summary>Convenience for logging an exception with a context label.</summary>
    public static void Write(string context, Exception ex) => Write($"{context}: {ex}");
}
