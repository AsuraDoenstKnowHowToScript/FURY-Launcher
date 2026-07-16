// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Services;

/// <summary>
/// A capped, in-memory log for a single instance run. Thread-safe: many game
/// threads append while the UI reads a snapshot and listens for new lines. Old
/// lines drop once the cap is reached, so memory stays bounded per session.
/// </summary>
public sealed class LogSession
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _lines = new();
    private readonly int _cap;

    public LogSession(string instanceId, int cap)
    {
        InstanceId = instanceId;
        _cap = cap < 1 ? 1 : cap;
    }

    /// <summary>The instance this session belongs to (empty for the general session).</summary>
    public string InstanceId { get; }

    /// <summary>
    /// Raised on the appending thread with the new line. Subscribers must marshal
    /// to their own thread; the UI enqueues and renders on a timer.
    /// </summary>
    public event Action<string>? LineAdded;

    /// <summary>Appends a line and drops the oldest ones past the cap.</summary>
    public void Append(string line)
    {
        lock (_gate)
        {
            _lines.AddLast(line);
            while (_lines.Count > _cap) _lines.RemoveFirst();
        }
        LineAdded?.Invoke(line);
    }

    /// <summary>Current lines, oldest first, as an immutable snapshot.</summary>
    public string[] Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}

/// <summary>
/// Owns one <see cref="LogSession"/> per instance id. The UI shows the session for
/// the selected instance; instances launched in the background keep writing to
/// their own session without touching the view, so switching instances switches
/// the log source instead of mixing runs together.
/// </summary>
public sealed class LogHub
{
    /// <summary>Key for the session shown before any instance is selected.</summary>
    public const string GeneralId = "";

    private readonly object _gate = new();
    private readonly Dictionary<string, LogSession> _sessions = new();
    private readonly int _cap;

    public LogHub(int perSessionCap = 1000) => _cap = perSessionCap;

    /// <summary>Returns the session for an instance id, creating it on first use.</summary>
    public LogSession Get(string? instanceId)
    {
        var key = instanceId ?? GeneralId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
                _sessions[key] = session = new LogSession(key, _cap);
            return session;
        }
    }

    /// <summary>Drops a session (e.g. when its instance is deleted).</summary>
    public void Remove(string instanceId)
    {
        lock (_gate) _sessions.Remove(instanceId);
    }
}
