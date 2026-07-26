// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Launcher.Core.Localization;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>One row in the Servers tab: the address, and whatever the server said about itself.</summary>
public sealed class ServerCardVm : ViewModelBase
{
    public ServerCardVm(ServerEntry entry)
    {
        Entry = entry;
        Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Address : entry.Name;
        // The game caches an icon in servers.dat, so a server the user has joined shows its
        // logo immediately instead of waiting for the ping to come back.
        Icon = Decode(entry.CachedIcon);
    }

    public ServerEntry Entry { get; }
    public string Address => Entry.Address;
    public bool IsPinned => Entry.Origin == ServerOrigin.Pinned;

    /// <summary>Which instance this was joined from, when it came from a servers.dat.</summary>
    public string? InstanceName => Entry.InstanceName;
    public bool HasInstance => !string.IsNullOrWhiteSpace(Entry.InstanceName);

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => SetProperty(ref _icon, value); }
    public bool HasIcon => Icon != null;

    private bool _querying = true;
    /// <summary>True from creation until the first ping answers, so the row can show it is working.</summary>
    public bool Querying { get => _querying; set { if (SetProperty(ref _querying, value)) Notify(); } }

    private ServerStatus? _status;
    public ServerStatus? Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            if (value?.Icon is { Length: > 0 } bytes)
            {
                var live = Decode(bytes);
                if (live != null) Icon = live;
            }
            Notify();
        }
    }

    public IReadOnlyList<MotdRun> Motd => Status?.Motd ?? Array.Empty<MotdRun>();
    public bool Online => Status?.Online == true;
    public bool Offline => Status != null && !Status.Online;
    public int Bars => Status?.Bars ?? 0;

    public string Players => Status is { Online: true }
        ? $"{Status.PlayersOnline:N0} / {Status.PlayersMax:N0}"
        : "—";

    public string Latency => Status is { Online: true } ? $"{Status.LatencyMs} ms" : "—";

    public string Version => Status is { Online: true } ? Status.VersionName : "";

    /// <summary>What to show instead of a MOTD when the server did not answer.</summary>
    public string OfflineText => Status?.Error is { Length: > 0 } e
        ? Loc.T("servers.offline") + " · " + e
        : Loc.T("servers.offline");

    private void Notify()
    {
        OnPropertyChanged(nameof(Motd));
        OnPropertyChanged(nameof(Online));
        OnPropertyChanged(nameof(Offline));
        OnPropertyChanged(nameof(Bars));
        OnPropertyChanged(nameof(Players));
        OnPropertyChanged(nameof(Latency));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(OfflineText));
        OnPropertyChanged(nameof(HasIcon));
    }

    private static Bitmap? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var raw = base64;
        var comma = raw.IndexOf(',');
        if (comma >= 0) raw = raw[(comma + 1)..];
        try { return Decode(Convert.FromBase64String(raw.Trim())); }
        catch { return null; }
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        // A server can send anything it likes here, so a bad image must not take the tab down.
        try { return new Bitmap(new MemoryStream(bytes)); }
        catch { return null; }
    }
}
