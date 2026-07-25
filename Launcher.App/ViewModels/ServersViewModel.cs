// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core;
using Launcher.Core.Localization;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// The Servers tab. Two lists: the servers the user has actually joined, read out of the game's
/// own servers.dat, and a directory of public servers grouped by region.
///
/// Everything displayed about a server — MOTD, players, version, icon, latency — is pinged from
/// the server itself over Minecraft's Server List Ping. Nothing here comes from a third-party
/// index, so nothing here can go stale or disagree with what the game would show.
/// </summary>
public sealed class ServersViewModel : ViewModelBase
{
    private readonly LauncherCore _core;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _inflight;

    public ServersViewModel(LauncherCore core, IDialogService dialogs)
    {
        _core = core;
        _dialogs = dialogs;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddCommand = new AsyncRelayCommand(AddAsync);
        RemoveCommand = new AsyncRelayCommand<ServerCardVm>(RemoveAsync);
        CopyAddressCommand = new AsyncRelayCommand<ServerCardVm>(CopyAddressAsync);
        SelectRegionCommand = new RelayCommand<RegionTabVm>(r => { if (r != null) SelectedRegion = r; });

        Regions = new ObservableCollection<RegionTabVm>(
            _core.Servers.Regions().Select(r => new RegionTabVm(r)));
        SelectedRegion = Regions.FirstOrDefault();

        Loc.Changed += RefreshTexts;
    }

    // ============================ lists ============================

    /// <summary>Servers joined from any instance, plus anything the user pinned by hand.</summary>
    public ObservableCollection<ServerCardVm> Mine { get; } = new();

    /// <summary>The shipped directory for the selected region.</summary>
    public ObservableCollection<ServerCardVm> Popular { get; } = new();

    public ObservableCollection<RegionTabVm> Regions { get; }

    private RegionTabVm? _selectedRegion;
    public RegionTabVm? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (!SetProperty(ref _selectedRegion, value)) return;
            foreach (var r in Regions) r.IsSelected = ReferenceEquals(r, value);
            _ = LoadPopularAsync();
        }
    }

    // ============================ state ============================

    private bool _busy;
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    private string _newAddress = "";
    public string NewAddress { get => _newAddress; set => SetProperty(ref _newAddress, value); }

    public bool MineEmpty => Mine.Count == 0 && !Busy;

    public string Title => Loc.T("nav.servers");
    public string Subtitle => Loc.T("servers.subtitle");
    public string MineHeader => Loc.T("servers.mine");
    public string PopularHeader => Loc.T("servers.popular");
    public string DirectoryNote => Loc.T("servers.directorynote");
    public string EmptyText => Loc.T("servers.empty");
    public string AddWatermark => Loc.T("servers.addwatermark");
    public string AddLabel => Loc.T("servers.add");
    public string RefreshLabel => Loc.T("btn.refresh");

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand AddCommand { get; }
    public IAsyncRelayCommand<ServerCardVm> RemoveCommand { get; }
    public IAsyncRelayCommand<ServerCardVm> CopyAddressCommand { get; }
    public IRelayCommand<RegionTabVm> SelectRegionCommand { get; }

    // ============================ loading ============================

    public async Task RefreshAsync()
    {
        // Leaving and re-entering the tab must not leave the previous round of pings running
        // against rows that no longer exist.
        _inflight?.Cancel();
        _inflight = new CancellationTokenSource();
        var ct = _inflight.Token;

        Busy = true;
        try
        {
            var played = await _core.Servers.PlayedAsync(ct);
            var pinned = await _core.Servers.PinnedAsync(ct);

            Mine.Clear();
            foreach (var entry in pinned.Concat(played))
                Mine.Add(new ServerCardVm(entry));
            OnPropertyChanged(nameof(MineEmpty));

            await Task.WhenAll(PingAsync(Mine, ct), LoadPopularAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh; the newer one owns the lists now.
        }
        catch (Exception ex)
        {
            _dialogs.Log("[servers] " + ex.Message);
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(MineEmpty));
        }
    }

    private async Task LoadPopularAsync(CancellationToken ct = default)
    {
        var region = SelectedRegion?.Region;
        if (region is null) return;

        Popular.Clear();
        foreach (var entry in _core.Servers.Directory(region))
            Popular.Add(new ServerCardVm(entry));

        await PingAsync(Popular, ct);
    }

    /// <summary>
    /// Pings a whole list and fills each row in as its answer lands, so a slow or dead server
    /// holds up only its own row rather than the page.
    /// </summary>
    private async Task PingAsync(IReadOnlyList<ServerCardVm> cards, CancellationToken ct)
    {
        var snapshot = cards.ToList();
        using var gate = new SemaphoreSlim(8);
        var jobs = snapshot.Select(async card =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var status = await _core.ServerPing.PingAsync(card.Address, ct);
                if (ct.IsCancellationRequested) return;
                // Pings finish on pool threads; property changes have to land on the UI thread
                // or the bindings update from the wrong one.
                Dispatcher.UIThread.Post(() =>
                {
                    card.Status = status;
                    card.Querying = false;
                });
            }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(jobs); }
        catch (OperationCanceledException) { }
    }

    // ============================ commands ============================

    private async Task AddAsync()
    {
        var address = (NewAddress ?? "").Trim();
        if (address.Length == 0) return;

        if (!await _core.Servers.PinAsync(address, address))
        {
            _dialogs.Toast(Loc.T("servers.already"));
            return;
        }
        NewAddress = "";
        await RefreshAsync();
    }

    private async Task RemoveAsync(ServerCardVm? card)
    {
        if (card == null || !card.IsPinned) return;
        await _core.Servers.UnpinAsync(card.Address);
        Mine.Remove(card);
        OnPropertyChanged(nameof(MineEmpty));
    }

    private async Task CopyAddressAsync(ServerCardVm? card)
    {
        if (card == null) return;
        // Joining is done inside the game, so the useful action here is handing the address over
        // ready to paste into the multiplayer screen.
        await _dialogs.CopyAsync(card.Address);
        _dialogs.Toast(Loc.T("servers.copied"));
    }

    private void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        foreach (var r in Regions) r.RefreshText();
    }
}

/// <summary>One region button above the directory list.</summary>
public sealed class RegionTabVm : ViewModelBase
{
    public RegionTabVm(ServerRegion region) => Region = region;

    public ServerRegion Region { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>Short code, the way a server list labels a region: NA, EU, ASIA, SA, OCE.</summary>
    public string Code => Region switch
    {
        ServerRegion.NorthAmerica => "NA",
        ServerRegion.Europe => "EU",
        ServerRegion.Asia => "ASIA",
        ServerRegion.SouthAmerica => "SA",
        ServerRegion.Oceania => "OCE",
        _ => "—",
    };

    public string Label => Loc.T("servers.region." + Region.ToString().ToLowerInvariant());

    public void RefreshText() => OnPropertyChanged(nameof(Label));
}
