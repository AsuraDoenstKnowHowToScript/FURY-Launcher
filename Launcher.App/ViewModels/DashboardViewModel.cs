// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core;
using Launcher.Core.Localization;
using Launcher.Core.Models;
using Launcher.Core.Services;

namespace Launcher.App.ViewModels;

/// <summary>
/// The dashboard: who you are, where your hours actually went, and what changed in the
/// launcher. Deliberately instance-centric — the headline is the user's own instances and
/// play time rather than a shelf of modpacks to install, which is what a launcher that
/// *manages* instances should lead with.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly LauncherCore _core;
    private readonly SelectedInstanceService _selected;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<ReleaseNote> _releases = Array.Empty<ReleaseNote>();

    public DashboardViewModel(LauncherCore core, SelectedInstanceService selected, IDialogService dialogs)
    {
        _core = core;
        _selected = selected;
        _dialogs = dialogs;

        PlayCommand = new RelayCommand(() =>
        {
            var inst = Featured?.Instance;
            if (inst != null) PlayRequested?.Invoke(this, inst);
        });
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty));
        OpenInstancesCommand = new RelayCommand(() => InstancesRequested?.Invoke(this, EventArgs.Empty));
        ShowAllCommand = new RelayCommand(() => ShowBetas = true);
        ShowStableCommand = new RelayCommand(() => ShowBetas = false);

        // A finished session changes the numbers; refresh when that happens.
        _core.Playtime.Changed += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        Loc.Changed += RefreshTexts;
    }

    // ------------------------------- intents -------------------------------

    /// <summary>Asks the shell to launch this instance (the shell owns the launch pipeline).</summary>
    public event EventHandler<Instance>? PlayRequested;

    /// <summary>Asks the shell to open the Content browser.</summary>
    public event EventHandler? BrowseRequested;

    /// <summary>Asks the shell to open the instance list.</summary>
    public event EventHandler? InstancesRequested;

    /// <summary>Raised when data changed underneath us and the shell should call RefreshAsync.</summary>
    public event EventHandler? RefreshRequested;

    public IRelayCommand PlayCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    public IRelayCommand OpenInstancesCommand { get; }
    public IRelayCommand ShowAllCommand { get; }
    public IRelayCommand ShowStableCommand { get; }

    // ------------------------------- content -------------------------------

    /// <summary>Instances ordered by time played, longest first.</summary>
    public ObservableCollection<InstanceTileVm> Instances { get; } = new();

    /// <summary>Seven columns, oldest first, ending today.</summary>
    public ObservableCollection<DayBarVm> Week { get; } = new();

    /// <summary>Release notes, newest first, filtered by the channel tab.</summary>
    public ObservableCollection<ChangelogItemVm> Changelog { get; } = new();

    private InstanceTileVm? _featured;
    /// <summary>The instance the big action button plays: the most recently played one.</summary>
    public InstanceTileVm? Featured
    {
        get => _featured;
        private set
        {
            if (!SetProperty(ref _featured, value)) return;
            OnPropertyChanged(nameof(HasFeatured));
            PlayCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasFeatured => _featured != null;

    private bool _hasInstances;
    public bool HasInstances
    {
        get => _hasInstances;
        private set { if (SetProperty(ref _hasInstances, value)) OnPropertyChanged(nameof(ShowPlaytimeHint)); }
    }

    private bool _hasPlaytime;
    /// <summary>False until something has actually been played, so we show a prompt instead of an empty chart.</summary>
    public bool HasPlaytime
    {
        get => _hasPlaytime;
        private set { if (SetProperty(ref _hasPlaytime, value)) OnPropertyChanged(nameof(ShowPlaytimeHint)); }
    }

    /// <summary>"You have instances but never played them" — the only state that needs a nudge.</summary>
    public bool ShowPlaytimeHint => _hasInstances && !_hasPlaytime;

    private bool _showBetas = true;
    /// <summary>Changelog channel tab: everything, or stable releases only.</summary>
    public bool ShowBetas
    {
        get => _showBetas;
        set
        {
            if (!SetProperty(ref _showBetas, value)) return;
            OnPropertyChanged(nameof(ShowStableOnly));
            ApplyChangelogFilter();
        }
    }

    public bool ShowStableOnly => !_showBetas;

    // -------------------------------- stats --------------------------------

    private string _userName = "";
    public string UserName { get => _userName; private set => SetProperty(ref _userName, value); }

    private string _greeting = "";
    public string Greeting { get => _greeting; private set => SetProperty(ref _greeting, value); }

    private string _totalPlayed = "0m";
    public string TotalPlayed { get => _totalPlayed; private set => SetProperty(ref _totalPlayed, value); }

    private string _instanceCount = "0";
    public string InstanceCount { get => _instanceCount; private set => SetProperty(ref _instanceCount, value); }

    private string _modCount = "0";
    public string ModCount { get => _modCount; private set => SetProperty(ref _modCount, value); }

    private string _lastSession = "—";
    public string LastSession { get => _lastSession; private set => SetProperty(ref _lastSession, value); }

    private string _weekTotal = "0m";
    public string WeekTotal { get => _weekTotal; private set => SetProperty(ref _weekTotal, value); }

    // ---------------------------- localized text ----------------------------

    public string SubtitleText => Loc.T("dash.subtitle");
    public string PlayText => Loc.T("dash.play");
    public string BrowseText => Loc.T("dash.browse");
    public string InstancesText => Loc.T("dash.instances");
    public string StatTotalText => Loc.T("dash.stat.total");
    public string StatInstancesText => Loc.T("dash.stat.instances");
    public string StatModsText => Loc.T("dash.stat.mods");
    public string StatLastText => Loc.T("dash.stat.last");
    public string HoursHeaderText => Loc.T("dash.hours");
    public string WeekHeaderText => Loc.T("dash.week");
    public string ChangelogHeaderText => Loc.T("dash.changelog");
    public string TabAllText => Loc.T("dash.tab.all");
    public string TabStableText => Loc.T("dash.tab.stable");
    public string NoDataText => Loc.T("dash.nodata");
    public string NoInstancesText => Loc.T("dash.noinstances");
    public string CurrentBuildText => Loc.T("dash.current");

    private void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        Greeting = GreetingFor(DateTime.Now.Hour);
    }

    // ------------------------------ behaviour ------------------------------

    /// <summary>Recomputes every tile. Cheap enough to call whenever the dashboard is shown.</summary>
    public async Task RefreshAsync()
    {
        Greeting = GreetingFor(DateTime.Now.Hour);

        var active = await _core.Accounts.GetActiveAsync();
        UserName = string.IsNullOrWhiteSpace(active?.Username) ? "" : active!.Username;

        var instances = (await _core.Instances.ListAsync()).ToList();
        InstanceCount = instances.Count.ToString();
        HasInstances = instances.Count > 0;

        var totalSeconds = await _core.Playtime.TotalAsync();
        TotalPlayed = Humanize(totalSeconds);
        HasPlaytime = totalSeconds > 0;

        // Per-instance seconds + mod counts. Mod counting hits the disk, so keep it off the
        // UI thread; awaiting resumes here, where the collections are safe to touch.
        var stats = await Task.Run(async () =>
        {
            var rows = new List<(Instance Inst, long Seconds, DateTime? Last, int Mods)>();
            foreach (var inst in instances)
            {
                var seconds = await _core.Playtime.ForInstanceAsync(inst.Id);
                var last = await _core.Playtime.LastPlayedAsync(inst.Id);
                int mods;
                try { mods = _core.Mods.ListMods(inst).Count(); } catch { mods = 0; }
                rows.Add((inst, seconds, last, mods));
            }
            return rows;
        });

        ModCount = stats.Sum(r => r.Mods).ToString();

        var busiest = stats.Count == 0 ? 0 : stats.Max(r => r.Seconds);
        Instances.Clear();
        foreach (var row in stats.OrderByDescending(r => r.Seconds).ThenBy(r => r.Inst.Name))
        {
            Instances.Add(new InstanceTileVm(
                row.Inst,
                row.Seconds,
                busiest > 0 ? (double)row.Seconds / busiest : 0,
                Humanize(row.Seconds),
                row.Last is { } l ? Relative(l) : Loc.T("dash.never")));
        }

        // The featured instance is whatever was played most recently; falling back to the
        // current selection, then to the first instance, so the button is never dead.
        var recent = stats.Where(r => r.Last != null).OrderByDescending(r => r.Last).FirstOrDefault();
        var featuredId = recent.Inst?.Id ?? _selected.Current?.Id ?? instances.FirstOrDefault()?.Id;
        Featured = Instances.FirstOrDefault(t => t.Id == featuredId) ?? Instances.FirstOrDefault();
        LastSession = recent.Last is { } lastPlayed ? Relative(lastPlayed) : "—";

        await BuildWeekAsync();
        await LoadChangelogAsync();
    }

    private async Task BuildWeekAsync()
    {
        var days = await _core.Playtime.LastSevenDaysAsync();
        var peak = days.Length == 0 ? 0 : days.Max();
        WeekTotal = Humanize(days.Sum());

        Week.Clear();
        for (var i = 0; i < days.Length; i++)
        {
            var date = DateTime.Now.Date.AddDays(-(days.Length - 1 - i));
            var label = date.ToString("ddd");
            Week.Add(new DayBarVm(
                label.Length > 0 ? label[..1].ToUpperInvariant() : "",
                days[i],
                peak > 0 ? (double)days[i] / peak : 0,
                date == DateTime.Now.Date,
                $"{label} · {Humanize(days[i])}"));
        }
    }

    private async Task LoadChangelogAsync()
    {
        if (_releases.Count == 0)
            _releases = await _core.Updates.ListReleasesAsync(AppInfo.RepoOwner, AppInfo.RepoName);
        ApplyChangelogFilter();
    }

    private void ApplyChangelogFilter()
    {
        Changelog.Clear();
        foreach (var r in _releases.Where(r => _showBetas || !r.IsBeta))
            Changelog.Add(new ChangelogItemVm(r, isCurrent: r.Version == AppInfo.Version));
    }

    // ------------------------------ formatting ------------------------------

    private static string GreetingFor(int hour) => Loc.T(hour switch
    {
        >= 5 and < 12 => "dash.greet.morning",
        >= 12 and < 18 => "dash.greet.afternoon",
        >= 18 and < 23 => "dash.greet.evening",
        _ => "dash.greet.night",
    });

    /// <summary>"2h 15m" / "45m" / "0m" — compact enough for a stat tile.</summary>
    private static string Humanize(long seconds)
    {
        if (seconds < 60) return "0m";
        var t = TimeSpan.FromSeconds(seconds);
        var hours = (int)t.TotalHours;
        return hours >= 1 ? $"{hours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    /// <summary>"just now" / "3h ago" / "2d ago" — relative, so it never needs a locale date.</summary>
    private static string Relative(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 2) return Loc.T("dash.justnow");
        if (span.TotalHours < 1) return Loc.T("dash.minsago", (int)span.TotalMinutes);
        if (span.TotalDays < 1) return Loc.T("dash.hoursago", (int)span.TotalHours);
        if (span.TotalDays < 30) return Loc.T("dash.daysago", (int)span.TotalDays);
        return utc.ToLocalTime().ToString("dd MMM yyyy");
    }
}
