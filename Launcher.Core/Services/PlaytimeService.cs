// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Services;

/// <summary>Accumulated play time for one instance.</summary>
public sealed class PlaytimeRecord
{
    public string InstanceId { get; set; } = "";

    /// <summary>Total seconds played, all time.</summary>
    public long TotalSeconds { get; set; }

    /// <summary>When the last session ended (UTC), or null if never played.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Seconds per local calendar day, keyed <c>yyyy-MM-dd</c>. Trimmed to the last ~60 days.</summary>
    public Dictionary<string, long> Daily { get; set; } = new();
}

/// <summary>
/// Measures how long each instance is actually played. Driven by
/// <see cref="GameLauncher.RunningChanged"/>: the clock starts when a process appears and the
/// elapsed time is banked when it exits, so the numbers come from real sessions rather than
/// from anything the UI guesses. Persisted to <c>playtime.json</c>. No UI, no network.
/// </summary>
public sealed class PlaytimeService
{
    /// <summary>Daily buckets older than this are dropped, so the file cannot grow forever.</summary>
    private const int KeepDays = 60;

    private readonly LauncherPaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _openSessions = new(); // instanceId -> started (UTC)
    private List<PlaytimeRecord>? _cache;

    public PlaytimeService(LauncherPaths paths) => _paths = paths;

    /// <summary>Raised after a session is banked, so a dashboard can refresh itself.</summary>
    public event EventHandler? Changed;

    /// <summary>Wire to <see cref="GameLauncher.RunningChanged"/>; never throws to the caller.</summary>
    public void OnRunningChanged(string instanceId, bool running)
    {
        if (string.IsNullOrEmpty(instanceId)) return;

        if (running)
        {
            lock (_gate) _openSessions[instanceId] = DateTime.UtcNow;
            return;
        }

        DateTime started;
        lock (_gate)
        {
            if (!_openSessions.TryGetValue(instanceId, out started)) return;
            _openSessions.Remove(instanceId);
        }

        var seconds = (long)Math.Round((DateTime.UtcNow - started).TotalSeconds);
        if (seconds <= 0) return;

        // Fire and forget: the game just exited, nobody is awaiting us.
        _ = BankAsync(instanceId, seconds);
    }

    private async Task BankAsync(string instanceId, long seconds)
    {
        try
        {
            var list = (await ListAsync().ConfigureAwait(false)).ToList();
            var rec = list.FirstOrDefault(r => r.InstanceId == instanceId);
            if (rec == null)
            {
                rec = new PlaytimeRecord { InstanceId = instanceId };
                list.Add(rec);
            }

            rec.TotalSeconds += seconds;
            rec.LastPlayedUtc = DateTime.UtcNow;

            var key = DateTime.Now.ToString("yyyy-MM-dd");
            rec.Daily[key] = rec.Daily.TryGetValue(key, out var d) ? d + seconds : seconds;
            Trim(rec);

            await SaveAsync(list).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CrashLog.Write("[playtime] banking a session failed", ex);
        }
    }

    /// <summary>Every stored record (empty when nothing has been played yet).</summary>
    public async Task<IReadOnlyList<PlaytimeRecord>> ListAsync(CancellationToken ct = default)
    {
        if (_cache != null) return _cache;
        var list = await JsonStore.ReadAsync<List<PlaytimeRecord>>(_paths.PlaytimeFile, ct).ConfigureAwait(false)
                   ?? new List<PlaytimeRecord>();
        _cache = list;
        return list;
    }

    /// <summary>Seconds played on one instance, including the session running right now.</summary>
    public async Task<long> ForInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        var stored = (await ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.InstanceId == instanceId)?.TotalSeconds ?? 0;
        return stored + LiveSeconds(instanceId);
    }

    /// <summary>Seconds played across every instance, including sessions running right now.</summary>
    public async Task<long> TotalAsync(CancellationToken ct = default)
    {
        var stored = (await ListAsync(ct).ConfigureAwait(false)).Sum(r => r.TotalSeconds);
        return stored + LiveSeconds(null);
    }

    /// <summary>When the given instance was last played (UTC), or null.</summary>
    public async Task<DateTime?> LastPlayedAsync(string instanceId, CancellationToken ct = default)
        => (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.InstanceId == instanceId)?.LastPlayedUtc;

    /// <summary>
    /// Seconds played on each of the last seven days, oldest first, ending today. Index 6 is
    /// today, so a bar chart can simply walk the array left to right.
    /// </summary>
    public async Task<long[]> LastSevenDaysAsync(CancellationToken ct = default)
    {
        var all = await ListAsync(ct).ConfigureAwait(false);
        var result = new long[7];
        for (var i = 0; i < 7; i++)
        {
            var key = DateTime.Now.Date.AddDays(-(6 - i)).ToString("yyyy-MM-dd");
            result[i] = all.Sum(r => r.Daily.TryGetValue(key, out var s) ? s : 0);
        }
        return result;
    }

    /// <summary>Elapsed time of sessions that are still open (so the UI is not stale mid-game).</summary>
    private long LiveSeconds(string? instanceId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _openSessions
                .Where(kv => instanceId == null || kv.Key == instanceId)
                .Sum(kv => (long)Math.Max(0, Math.Round((now - kv.Value).TotalSeconds)));
        }
    }

    private static void Trim(PlaytimeRecord rec)
    {
        if (rec.Daily.Count <= KeepDays) return;
        var cutoff = DateTime.Now.Date.AddDays(-KeepDays).ToString("yyyy-MM-dd");
        foreach (var stale in rec.Daily.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            rec.Daily.Remove(stale);
    }

    private async Task SaveAsync(List<PlaytimeRecord> list)
    {
        _cache = list;
        await JsonStore.WriteAsync(_paths.PlaytimeFile, list).ConfigureAwait(false);
    }
}
