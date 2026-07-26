// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core;
using Launcher.Core.Localization;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// Browsing and installing Modrinth modpacks. A pack is a Minecraft version, a loader and a mod
/// list as one unit, which is why installing one is a decision and not just a download: it either
/// becomes a new instance, or it takes over an instance that already exists. That choice is made
/// once, in the header, rather than being asked again on every card.
/// </summary>
public sealed class ModpacksViewModel : ViewModelBase
{
    private readonly LauncherCore _core;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _inflight;

    public ModpacksViewModel(LauncherCore core, IDialogService dialogs)
    {
        _core = core;
        _dialogs = dialogs;

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        InstallCommand = new AsyncRelayCommand<ModpackCardVm>(InstallAsync);

        Loc.Changed += () => OnPropertyChanged(string.Empty);
    }

    // ============================ state ============================

    public ObservableCollection<ModpackCardVm> Results { get; } = new();

    /// <summary>Where the next install goes: a new instance, or one that already exists.</summary>
    public ObservableCollection<InstallTargetVm> Targets { get; } = new();

    private InstallTargetVm? _selectedTarget;
    public InstallTargetVm? SelectedTarget
    {
        get => _selectedTarget;
        set => SetProperty(ref _selectedTarget, value);
    }

    private string _query = "";
    public string Query { get => _query; set => SetProperty(ref _query, value); }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(Empty)); }
    }

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool Empty => Results.Count == 0 && !Busy;

    public string Title => Loc.T("nav.modpacks");
    public string Subtitle => Loc.T("modpacks.subtitle");
    public string BrowseHeader => Loc.T("modpacks.browse");
    public string TargetLabel => Loc.T("modpacks.target");
    public string SearchLabel => Loc.T("btn.search");
    public string InstallLabel => Loc.T("modpacks.install");
    public string SearchWatermark => Loc.T("modpacks.watermark");
    public string EmptyText => Loc.T("modpacks.empty");

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand<ModpackCardVm> InstallCommand { get; }

    // ============================ loading ============================

    /// <summary>Called when the tab opens: refreshes install targets and shows popular packs.</summary>
    public async Task EnterAsync()
    {
        await RefreshTargetsAsync();
        if (Results.Count == 0) await SearchAsync();
    }

    private async Task RefreshTargetsAsync()
    {
        var keep = SelectedTarget?.Instance?.Id;
        var instances = await _core.Instances.ListAsync();

        Targets.Clear();
        Targets.Add(new InstallTargetVm(null));
        foreach (var i in instances) Targets.Add(new InstallTargetVm(i));

        SelectedTarget = Targets.FirstOrDefault(t => t.Instance?.Id == keep) ?? Targets[0];
    }

    private async Task SearchAsync()
    {
        _inflight?.Cancel();
        _inflight = new CancellationTokenSource();
        var ct = _inflight.Token;

        Busy = true;
        Status = "";
        try
        {
            // No version filter: a pack carries its own, so filtering the browse by one would
            // hide most of the catalogue for no benefit.
            var hits = await _core.Mods.SearchModpacksAsync(Query ?? "", ct);
            if (ct.IsCancellationRequested) return;

            Results.Clear();
            foreach (var hit in hits) Results.Add(new ModpackCardVm(hit));
            OnPropertyChanged(nameof(Empty));

            foreach (var card in Results.ToList()) _ = LoadIconAsync(card, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            _dialogs.Log("[modpacks] " + ex.Message);
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(Empty));
        }
    }

    private static async Task LoadIconAsync(ModpackCardVm card, CancellationToken ct)
    {
        var bmp = await RemoteIcons.GetAsync(card.IconUrl, ct);
        if (bmp != null && !ct.IsCancellationRequested)
            Dispatcher.UIThread.Post(() => card.Icon = bmp);
    }

    // ============================ installing ============================

    private async Task InstallAsync(ModpackCardVm? card)
    {
        if (card == null || card.Installing) return;
        var target = SelectedTarget ?? Targets.FirstOrDefault();
        if (target == null) return;

        await _dialogs.RunGuardedAsync(async () =>
        {
            card.Installing = true;
            var download = Path.Combine(Path.GetTempPath(), $"bonfire-{Guid.NewGuid():N}.mrpack");
            try
            {
                card.Progress = Loc.T("modpacks.resolving");
                var version = (await _core.Mods.GetModpackVersionsAsync(card.ProjectId)).FirstOrDefault()
                    ?? throw new InvalidOperationException(Loc.T("modpacks.noversion"));

                var file = version.Files.FirstOrDefault(f => f.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                           ?? version.Files.FirstOrDefault(f => f.Primary)
                           ?? throw new InvalidOperationException(Loc.T("modpacks.nofile"));

                card.Progress = Loc.T("modpacks.downloading");
                await _core.Mods.DownloadFileAsync(file.Url, download);

                var info = await _core.Mrpacks.ReadInfoAsync(download);

                // Taking over an existing instance changes what that instance is, so it is asked
                // rather than assumed. Creating a new one needs no confirmation: nothing is lost.
                if (target.Instance is { } instance)
                {
                    var confirmed = await _dialogs.ConfirmAsync(
                        Loc.T("modpacks.confirm.title"),
                        Loc.T("modpacks.confirm.body", instance.Name, info.Name,
                              info.McVersion, info.Loader.ToString(), info.FileCount.ToString()),
                        Loc.T("modpacks.install"));
                    if (!confirmed) return;
                }

                var progress = new Progress<(int done, int total)>(p => Dispatcher.UIThread.Post(() =>
                    card.Progress = p.total > 0 ? $"{p.done}/{p.total}" : ""));

                var result = target.Instance is { } existing
                    ? await _core.Mrpacks.InstallIntoAsync(existing, download, progress)
                    : await _core.Mrpacks.ImportAsync(download, progress);

                _dialogs.Toast(Loc.T("modpacks.done", result.Name));
                InstalledInto?.Invoke(this, result);
                await RefreshTargetsAsync();
            }
            finally
            {
                card.Installing = false;
                card.Progress = "";
                try { if (File.Exists(download)) File.Delete(download); } catch { /* temp file */ }
            }
        });
    }

    /// <summary>Raised after a pack lands, so the window can refresh the instance list.</summary>
    public event EventHandler<Instance>? InstalledInto;
}

/// <summary>An entry in the "install into" picker: a new instance, or an existing one.</summary>
public sealed class InstallTargetVm
{
    public InstallTargetVm(Instance? instance) => Instance = instance;

    public Instance? Instance { get; }

    public string Label => Instance is null
        ? Loc.T("modpacks.newinstance")
        : $"{Instance.Name}  ·  {Instance.McVersion} {Instance.Loader}";

    public override string ToString() => Label;
}

/// <summary>One modpack in the browse grid.</summary>
public sealed class ModpackCardVm : ViewModelBase
{
    public ModpackCardVm(ModrinthHit hit)
    {
        Hit = hit;
        Downloads = FormatCount(hit.Downloads);
    }

    public ModrinthHit Hit { get; }
    public string ProjectId => Hit.ProjectId;
    public string Title => Hit.Title;
    public string Author => Hit.Author ?? "";
    public string Description => Hit.Description;
    public string? IconUrl => Hit.IconUrl;
    public string Downloads { get; }

    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon;
        set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
    }
    public bool HasIcon => _icon != null;

    private bool _installing;
    public bool Installing
    {
        get => _installing;
        set { if (SetProperty(ref _installing, value)) OnPropertyChanged(nameof(Idle)); }
    }
    public bool Idle => !_installing;

    private string _progress = "";
    public string Progress
    {
        get => _progress;
        set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(HasProgress)); }
    }
    public bool HasProgress => _progress.Length > 0;

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#") + "M",
        >= 1_000 => (n / 1_000d).ToString("0.#") + "k",
        _ => n.ToString(),
    };
}
