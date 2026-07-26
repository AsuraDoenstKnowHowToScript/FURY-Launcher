// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Launcher.App.Services;

/// <summary>
/// Fetches and caches the small artwork a listing shows. Icons are decorative: a failure keeps
/// the placeholder rather than surfacing anything, because a missing picture is not something a
/// user can act on.
/// </summary>
public static class RemoteIcons
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>A search result is dozens of icons at once; without a gate they all start together.</summary>
    private static readonly SemaphoreSlim Gate = new(6);

    private static readonly Dictionary<string, Bitmap> Cache = new();

    /// <summary>
    /// Returns the icon for a URL, from cache when possible. Always resolves on the UI thread,
    /// since the result is assigned straight to a bound property.
    /// </summary>
    public static async Task<Bitmap?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Cache) if (Cache.TryGetValue(url, out var hit)) return hit;

        try { await Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            // Another card with the same icon may have fetched it while we queued.
            lock (Cache) if (Cache.TryGetValue(url, out var now)) return now;

            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;

            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Bitmap bmp;
                try { bmp = new Bitmap(new MemoryStream(bytes)); }
                catch { return (Bitmap?)null; }
                lock (Cache)
                {
                    // Bounded rather than growing forever. Icons still on screen stay alive
                    // through their view models; evicted ones simply fetch again.
                    if (Cache.Count > 256) Cache.Clear();
                    Cache[url] = bmp;
                }
                return bmp;
            });
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
