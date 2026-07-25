// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Assembles the three lists the Servers tab shows: the servers the user has actually joined,
/// the ones they pinned by hand, and a small shipped directory grouped by region.
///
/// The directory is a starting point, not a ranking. There is no free, accurate "top servers by
/// region" feed worth depending on, and scraping a server-list site would make the launcher
/// break whenever that site changed its markup. So the addresses are shipped, everything shown
/// about them is pinged live from the server itself, and anything the user adds is kept next to
/// them. A shipped server that has shut down simply shows as offline instead of lying.
/// </summary>
public sealed class ServerDirectory
{
    private readonly LauncherPaths _paths;
    private readonly IInstanceService _instances;

    public ServerDirectory(LauncherPaths paths, IInstanceService instances)
    {
        _paths = paths;
        _instances = instances;
    }

    /// <summary>
    /// Long-running public servers, grouped by where they are hosted. Deliberately short: a
    /// handful of addresses that have been up for years beats a long list that rots.
    /// </summary>
    private static readonly ServerEntry[] Shipped =
    {
        new() { Name = "Hypixel",    Address = "mc.hypixel.net",       Region = ServerRegion.NorthAmerica, Origin = ServerOrigin.Directory },
        new() { Name = "Wynncraft",  Address = "play.wynncraft.com",   Region = ServerRegion.NorthAmerica, Origin = ServerOrigin.Directory },
        new() { Name = "ManaCube",   Address = "play.manacube.com",    Region = ServerRegion.NorthAmerica, Origin = ServerOrigin.Directory },
        new() { Name = "2b2t",       Address = "2b2t.org",             Region = ServerRegion.NorthAmerica, Origin = ServerOrigin.Directory },

        new() { Name = "CubeCraft",  Address = "play.cubecraft.net",   Region = ServerRegion.Europe,       Origin = ServerOrigin.Directory },
        new() { Name = "GommeHD",    Address = "play.gommehd.net",     Region = ServerRegion.Europe,       Origin = ServerOrigin.Directory },
        new() { Name = "PikaNetwork",Address = "play.pika-network.net",Region = ServerRegion.Europe,       Origin = ServerOrigin.Directory },
        new() { Name = "Minemen",    Address = "eu.minemen.club",      Region = ServerRegion.Europe,       Origin = ServerOrigin.Directory },

        new() { Name = "Hypixel Asia",  Address = "mc.hypixel.net",    Region = ServerRegion.Asia,         Origin = ServerOrigin.Directory },
        new() { Name = "Loyisa",        Address = "mc.loyisa.cn",      Region = ServerRegion.Asia,         Origin = ServerOrigin.Directory },

        // Checked against live DNS. All three publish only SRV and point their A record at a CDN,
        // so they are also the case that proves the SRV lookup is doing its job.
        new() { Name = "Craftlandia", Address = "craftlandia.com.br",  Region = ServerRegion.SouthAmerica, Origin = ServerOrigin.Directory },
        new() { Name = "MushMC",      Address = "mushmc.com",          Region = ServerRegion.SouthAmerica, Origin = ServerOrigin.Directory },
        new() { Name = "Hylex",       Address = "hylex.gg",            Region = ServerRegion.SouthAmerica, Origin = ServerOrigin.Directory },
    };

    /// <summary>The shipped directory for one region, or all of it when region is null.</summary>
    public IReadOnlyList<ServerEntry> Directory(ServerRegion? region = null) =>
        region is null ? Shipped : Shipped.Where(s => s.Region == region).ToList();

    /// <summary>Regions that actually have entries, in display order.</summary>
    public IReadOnlyList<ServerRegion> Regions() =>
        new[]
        {
            ServerRegion.NorthAmerica, ServerRegion.Europe,
            ServerRegion.Asia, ServerRegion.SouthAmerica, ServerRegion.Oceania,
        }
        .Where(r => Shipped.Any(s => s.Region == r)).ToList();

    /// <summary>
    /// Every server the user has joined, read from each instance's servers.dat and de-duplicated
    /// by address. The same server joined from two instances is one entry, labelled with the
    /// instance it was most recently found in.
    /// </summary>
    public async Task<IReadOnlyList<ServerEntry>> PlayedAsync(CancellationToken ct = default)
    {
        var instances = await _instances.ListAsync(ct).ConfigureAwait(false);
        var seen = new Dictionary<string, ServerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            foreach (var entry in ServersDatReader.Read(_paths.InstanceServersDat(instance), instance.Name))
            {
                var key = Normalize(entry.Address);
                // Later instances win, so the label names somewhere the server still exists.
                seen[key] = entry;
            }
        }
        return seen.Values.ToList();
    }

    /// <summary>Servers the user added in the Servers tab.</summary>
    public async Task<List<ServerEntry>> PinnedAsync(CancellationToken ct = default)
        => await JsonStore.ReadAsync<List<ServerEntry>>(_paths.PinnedServersFile, ct).ConfigureAwait(false)
           ?? new List<ServerEntry>();

    /// <summary>Adds a pinned server. Adding one that is already pinned is a no-op, not an error.</summary>
    public async Task<bool> PinAsync(string name, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var list = await PinnedAsync(ct).ConfigureAwait(false);
        var key = Normalize(address);
        if (list.Any(s => Normalize(s.Address) == key)) return false;

        list.Add(new ServerEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? address.Trim() : name.Trim(),
            Address = address.Trim(),
            Origin = ServerOrigin.Pinned,
        });
        await JsonStore.WriteAsync(_paths.PinnedServersFile, list, ct).ConfigureAwait(false);
        return true;
    }

    public async Task UnpinAsync(string address, CancellationToken ct = default)
    {
        var list = await PinnedAsync(ct).ConfigureAwait(false);
        var key = Normalize(address);
        list.RemoveAll(s => Normalize(s.Address) == key);
        await JsonStore.WriteAsync(_paths.PinnedServersFile, list, ct).ConfigureAwait(false);
    }

    /// <summary>Host and port, lower-cased, so "MC.Hypixel.net" and "mc.hypixel.net:25565" match.</summary>
    private static string Normalize(string address)
    {
        var (host, port) = ServerPingService.SplitAddress(address);
        return $"{host.ToLowerInvariant()}:{port}";
    }
}
