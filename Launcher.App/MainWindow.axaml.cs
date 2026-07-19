// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CmlLib.Core.Auth;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core;
using Launcher.Core.Localization;
using Launcher.Core.Models;
using Launcher.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App;

/// <summary>
/// The entire (bare) UI. All real work is delegated to <see cref="LauncherCore"/>;
/// this file only reads/writes controls and marshals Core events to the UI thread.
/// All visible text comes from <see cref="Loc"/> via <see cref="ApplyLanguage"/>.
/// </summary>
public partial class MainWindow : AppWindow
{
    private readonly LauncherCore _core = new();
    private readonly LoaderType[] _loaders = Enum.GetValues<LoaderType>();

    private List<Instance> _instances = new();
    private List<ModItem> _mods = new();
    private readonly List<InstalledModVm> _allModVms = new();      // full set for the current instance
    private readonly ObservableCollection<InstalledModVm> _modVms = new(); // filtered, bound to the list
    private CancellationTokenSource? _modEnrichCts; // cancels stale metadata enrichment on refresh
    private List<OfflineProfile> _profiles = new();
    private readonly ObservableCollection<ModrinthHitVm> _modrinthVms = new();
    private IReadOnlyList<ModrinthVersion> _modVersions = new List<ModrinthVersion>();
    private InstalledSignatures? _installedSigs; // what the selected instance already has, for the "Installed" tag
    private ContentKind _contentKind = ContentKind.Mod; // Mods / Shaders / Datapacks segment
    private ContentSource _contentSource = ContentSource.Modrinth; // Modrinth / CurseForge browser source

    // Modrinth search: debounced query, page-by-scroll, cancel-on-new-search.
    private DispatcherTimer? _searchDebounce;
    private CancellationTokenSource? _modrinthCts;
    private string _modrinthQuery = "";
    private int _modrinthOffset;
    private bool _modrinthHasMore;
    private bool _modrinthLoading;
    private static readonly HttpClient _iconHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, Bitmap> _iconCache = new();
    // Cap concurrent icon downloads so 30 result thumbnails don't starve the
    // bandwidth the search/next-page requests need.
    private static readonly SemaphoreSlim _iconGate = new(6);

    private Instance? _editing;      // instance selected for editing (Instances tab)
    private MSession? _msSession;    // cached Microsoft session after login
    private CancellationTokenSource? _launchCts;
    private bool _suppressLangEvent; // guards the language dropdown during programmatic set
    private List<string> _versions = new(); // Minecraft versions shown in the create/edit dropdown
    private UpdateInfo? _pendingUpdate;     // newer release found on GitHub, if any

    // Java runtimes detected on this machine, shared by the Settings default picker
    // and the per-instance Java dropdown. Index 0 in both combos is always "Auto".
    private IReadOnlyList<JavaLocator.JavaRuntime> _javaRuntimes = Array.Empty<JavaLocator.JavaRuntime>();
    private bool _suppressJavaEvents; // guards the Java combos during programmatic fills
    private readonly SelectedInstanceService _selected = App.Services.GetRequiredService<SelectedInstanceService>();

    // Coalesced UI updates. The game/installer raise log + progress events far too
    // fast to touch the UI once per event (doing so froze the window into "not
    // responding"). We buffer here from any thread and flush on a UI timer, and cap
    // the log so its TextBox Text never grows unbounded (which was O(n²) per line).
    private readonly object _uiLock = new();
    private readonly Queue<string> _pendingLog = new();
    private readonly LinkedList<string> _logLines = new();
    private const int MaxLogLines = 500;

    // The log panel mirrors ONE instance's session at a time. Instances launched
    // in the background keep filling their own session; switching the selection
    // swaps which session is shown (see ShowLogFor), never mixing runs.
    private LogSession? _displayedSession;
    private Action<string>? _sessionSink;
    private double? _pendingProgress;
    private string? _pendingStatus;
    private DispatcherTimer? _uiTimer;

    public MainWindow()
    {
        InitializeComponent();

        Title = $"{AppInfo.Name} v{AppInfo.Version}";
        AboutTitle.Text = AppInfo.Name;

        LoaderCombo.ItemsSource = _loaders.Select(l => l.ToString()).ToList();
        LoaderCombo.SelectedIndex = 0;
        StopButton.IsEnabled = false;

        // Language selector (About tab). Endonyms don't change with the UI language.
        LanguageCombo.ItemsSource = LanguageInfo.All.Select(LanguageInfo.NativeName).ToList();
        LanguageCombo.SelectedIndex = Array.IndexOf(LanguageInfo.All, Loc.Current);
        LanguageCombo.SelectionChanged += OnLanguageChanged;
        Loc.Changed += () => Dispatcher.UIThread.Post(ApplyLanguage);
        ApplyLanguage();

        // --- wire events ---
        InstancesList.SelectionChanged += OnInstanceSelected;
        InstancesList.DoubleTapped += OnInstanceDoubleTapped;
        RefreshInstancesButton.Click += async (_, _) => await SafeAsync(RefreshInstancesAsync);
        AddInstanceButton.Click += (_, _) => OpenInstanceDialog(isNew: true);
        EditInstanceButton.Click += (_, _) => OpenInstanceDialog(isNew: false);
        CancelInstanceButton.Click += (_, _) => InstanceOverlay.IsVisible = false;
        OpenFolderButton.Click += OnOpenInstanceFolder;
        NewInstanceButton.Click += OnCreateInstance;
        SaveInstanceButton.Click += OnSaveInstance;
        BrowseJavaButton.Click += OnBrowseJava;
        DetectJavaButton.Click += async (_, _) => await SafeAsync(DetectJavaAsync);
        DefaultJavaCombo.SelectionChanged += OnDefaultJavaChanged;
        JavaCombo.SelectionChanged += OnInstanceJavaChanged;

        MicrosoftLoginButton.Click += OnMicrosoftLogin;
        MicrosoftLogoutButton.Click += OnMicrosoftLogout;
        PlayButton.Click += OnPlay;
        StopButton.Click += (_, _) =>
        {
            _launchCts?.Cancel();
            var id = _selected.Current?.Id;
            if (id != null) _core.Game.Stop(id);
        };

        ModInstanceCombo.SelectionChanged += (_, _) => RefreshMods();
        RefreshModsButton.Click += (_, _) => RefreshMods();
        AddModButton.Click += OnAddMod;
        ModsFilterBox.TextChanged += (_, _) => ApplyModFilter();
        // One-shot fade as each card is realized (installed list + search results).
        ModsList.ContainerPrepared += OnModContainerPrepared;
        ModrinthList.ContainerPrepared += OnModContainerPrepared;
        // Content type segment: Mods / Shaders / Datapacks.
        SegMods.IsCheckedChanged += OnContentSegmentChanged;
        SegShaders.IsCheckedChanged += OnContentSegmentChanged;
        SegDatapacks.IsCheckedChanged += OnContentSegmentChanged;
        // Search source: Modrinth is live; CurseForge is temporarily "coming soon" while
        // its integration is finished (the client + key plumbing stay for when it returns).
        SrcModrinth.IsCheckedChanged += OnSourceChanged;
        SrcCurseForge.IsCheckedChanged += OnSourceChanged;
        SrcCurseForge.IsEnabled = false;
        ToolTip.SetTip(SrcCurseForge, Loc.T("source.soon"));
        ModrinthList.ItemsSource = _modrinthVms;
        ModrinthSearchButton.Click += (_, _) => StartModrinthSearch();
        ModrinthList.SelectionChanged += OnModrinthResultSelected;
        ModrinthDownloadButton.Click += OnModrinthDownload;
        // Debounced search: type-to-search after a short pause; Enter searches now.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce!.Stop(); StartModrinthSearch(); };
        ModrinthQueryBox.TextChanged += (_, _) => { _searchDebounce!.Stop(); _searchDebounce!.Start(); };
        ModrinthQueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _searchDebounce!.Stop(); StartModrinthSearch(); }
        };
        // Infinite scroll: fetch the next page when scrolled near the bottom.
        ModrinthList.AddHandler(ScrollViewer.ScrollChangedEvent, OnModrinthScroll);
        // Left navigation: swap the visible section; refresh mods when opening Mods.
        NavView.SelectionChanged += OnNavSelectionChanged;
        NavVersion.Text = "v" + AppInfo.Version;
        NavView.SelectedItem = NavHome;

        // --- modpack (.frpack) + skin profiles ---
        // Import/Export/Delete Click handlers are wired in XAML (they live inside
        // flyouts, a separate namescope, so no code-behind field is generated).
        SkinProfileCombo.SelectionChanged += OnSkinProfileSelected;
        NewProfileButton.Click += OnNewProfile;
        SaveProfileButton.Click += OnSaveProfile;
        DeleteProfileButton.Click += OnDeleteProfile;
        ChooseSkinButton.Click += OnChooseSkin;
        ChooseCapeButton.Click += OnChooseCape;
        ApplySkinButton.Click += OnApplySkin;

        // --- empty-state + Vanilla-mods shortcuts ---
        EmptyStateNewButton.Click += OnEmptyStateNew;
        NewModdedInstanceButton.Click += OnCreateModdedInstance;

        // --- auto-update banner ---
        UpdateButton.Click += OnUpdateClick;
        UpdateDismissButton.Click += (_, _) => UpdateBanner.IsVisible = false;
        CheckUpdatesButton.Click += OnCheckUpdates;

        // --- Core → UI: buffer high-frequency events; a timer flushes them ---
        // Auth diagnostics (browser open, OAuth errors) go to the log panel and crash.log.
        _core.Auth.Log += (_, line) => { AppendLog(line); CrashLog.Write(line); };
        // Interactive MS login: prefer the embedded WebView2 window (automatic, no paste);
        // fall back to the paste dialog only when the WebView2 runtime is unavailable.
        _core.Auth.WebUiFactory = () => WebView2LoginWebUi.IsRuntimeAvailable()
            ? new WebView2LoginWebUi(
                line => { AppendLog(line); CrashLog.Write(line); },
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             AppInfo.DataFolderName, "webview2"))
            : null;
        _core.Auth.InteractivePrompt = PromptForAuthCodeAsync;
        _core.Game.ByteProgress += (_, p) => { lock (_uiLock) _pendingProgress = p.Ratio; };
        _core.Game.FileProgress += (_, f) =>
        {
            lock (_uiLock) _pendingStatus = $"{f.EventType}: {f.Name} ({f.Progressed}/{f.Total})";
        };
        // Game/installer log lines are written straight to each instance's session
        // by the Core; the UI only listens to the session it is currently showing.
        // RunningChanged is rare and per-instance, so update the controls immediately
        // but only when the event is about the instance the user has selected.
        _core.Game.RunningChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.InstanceId == (_selected.Current?.Id ?? "")) ApplyRunState(e.Running);
        });

        // Start on the general session so pre-selection logs (auth, updates) still show.
        ShowLogFor(LogHub.GeneralId);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _ = SafeAsync(InitAsync);
    }

    // ============================ LOCALIZATION ============================

    /// <summary>Applies every static UI string from <see cref="Loc"/> for the current language.</summary>
    private void ApplyLanguage()
    {
        // Tabs
        NavHome.Content = Loc.T("nav.home");
        NavMods.Content = Loc.T("nav.content");
        NavAccounts.Content = Loc.T("nav.accounts");
        NavSettings.Content = Loc.T("nav.settings");
        LblJavaSection.Text = Loc.T("settings.java");
        DetectJavaButton.Content = Loc.T("btn.detectjava");
        RebuildJavaCombos(); // refresh the "Auto" label in the current language

        // Instances tab
        LblInstances.Text = Loc.T("instances.list");
        AddInstanceLabel.Text = Loc.T("home.newtitle");
        EditInstanceButton.Content = Loc.T("btn.edit");
        OpenFolderButton.Content = Loc.T("btn.folder");
        CancelInstanceButton.Content = Loc.T("btn.cancel");
        RefreshInstancesButton.Content = Loc.T("btn.refresh");
        LblName.Text = Loc.T("field.name");
        LblMcVersion.Text = Loc.T("field.mcversion");
        LblLoader.Text = Loc.T("field.loader");
        LblMinRam.Text = Loc.T("field.minram");
        LblMaxRam.Text = Loc.T("field.maxram");
        LblJvmArgs.Text = Loc.T("field.jvmargs");
        LblJavaPath.Text = Loc.T("field.javapath");
        NewInstanceButton.Content = Loc.T("btn.createnew");
        SaveInstanceButton.Content = Loc.T("btn.saveedit");
        LblSecGeneral.Text = Loc.T("section.general");
        LblSecMemory.Text = Loc.T("section.memoryjava");
        ImportButtonLabel.Text = Loc.T("btn.import");
        // Flyout menu items have no code-behind field (popup namescope): set headers
        // by walking each named parent button's flyout.
        SetFlyoutHeaders(ImportButton, Loc.T("btn.importpack"), Loc.T("btn.importmrpack"));
        SetFlyoutHeaders(MoreActionsButton, Loc.T("btn.exportpack"), Loc.T("btn.delete"));

        // Play tab
        LblProfileOffline.Text = Loc.T("label.profile");
        RefreshPlayAccountList();
        MicrosoftLoginLabel.Text = Loc.T("btn.mslogin");
        MicrosoftLogoutButton.Content = Loc.T("btn.mslogout");
        PlayButtonLabel.Text = Loc.T("btn.play");
        StopButton.Content = Loc.T("btn.stop");
        LblLog.Text = Loc.T("label.log");
        AccountStatus.Text = _msSession != null
            ? Loc.T("account.ms", _msSession.Username)
            : Loc.T("status.offlineparen");

        // Mods tab
        LblModInstance.Text = Loc.T("label.instance");
        RefreshModsButton.Content = Loc.T("btn.refresh");
        UpdateContentTitle();
        ModsFilterBox.Watermark = Loc.T("mods.filter");
        ModsEmptyText.Text = Loc.T("mods.noinstalled");
        AddModButton.Content = Loc.T("btn.addjar");
        ModrinthQueryBox.Watermark = Loc.T("watermark.search", SourceName());
        ModrinthSearchButton.Content = Loc.T("btn.search");
        SrcCurseForgeLabel.Text = "CurseForge · " + Loc.T("source.soon");
        LblModVersion.Text = Loc.T("label.version");
        ModrinthDownloadButton.Content = Loc.T("btn.downloadinstance");
        ModrinthEmptyText.Text = Loc.T("mods.searchprompt");

        // Skin tab: same profile/nick wording as the Play tab.
        LblSkinAccountHeader.Text = Loc.T("skins.account");
        LblSkinProfileHeader.Text = Loc.T("skins.profile");
        LblSkinAppearanceHeader.Text = Loc.T("skins.appearance");
        NewProfileButton.Content = Loc.T("btn.new");
        DeleteProfileButton.Content = Loc.T("btn.deleteprofile");
        LblProfileName.Text = Loc.T("label.nick");
        LblNickHint.Text = Loc.T("skins.nickhint");
        SlimCheck.Content = Loc.T("check.slim");
        SaveProfileButton.Content = Loc.T("btn.saveprofile");
        ChooseSkinButton.Content = Loc.T("btn.chooseskin");
        ChooseCapeButton.Content = Loc.T("btn.choosecape");
        LblApplyInstance.Text = Loc.T("label.applyinstance");
        ApplySkinButton.Content = Loc.T("btn.applyingame");
        LblSkinPreview.Text = Loc.T("label.skinpreview");
        LblFacePreview.Text = Loc.T("label.facepreview");
        LblCapePreview.Text = Loc.T("label.capepreview");
        LblSkinHelp1.Text = Loc.T("skin.help1");
        LblSkinHelp2.Text = Loc.T("skin.help2");

        // About tab
        AboutVersion.Text = Loc.T("about.version", AppInfo.Version);
        AboutCopyright.Text = Loc.T("about.license");
        AboutContact.Text = Loc.T("about.contact");
        LblLanguage.Text = Loc.T("label.language");
        CheckUpdatesButton.Content = Loc.T("btn.checkupdates");

        // Empty-state + Vanilla-mods notices
        EmptyStateText.Text = Loc.T("empty.noinstances");
        EmptyStateNewButton.Content = Loc.T("empty.newinstance");
        ModsVanillaWarning.Text = Loc.T("mods.vanillawarn");
        NewModdedInstanceButton.Content = Loc.T("mods.createmodded");

        // Re-evaluate Vanilla-dependent enable/disable + tooltips with the (new) language.
        UpdateModsAvailability();
        RefreshUpdateBanner();
    }

    /// <summary>Sets a button's MenuFlyout item headers in order (flyout items get no
    /// generated code-behind field, so they are reached through the parent button).</summary>
    private static void SetFlyoutHeaders(Button host, params string[] headers)
    {
        if (host.Flyout is not MenuFlyout mf) return;
        int i = 0;
        foreach (var item in mf.Items)
        {
            if (i >= headers.Length) break;
            if (item is MenuItem mi) mi.Header = headers[i];
            i++;
        }
    }

    /// <summary>Left-nav navigation: show the chosen section, hide the rest.</summary>
    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        var item = e.SelectedItem as NavigationViewItem;
        HomePanel.IsVisible = ReferenceEquals(item, NavHome);
        ModsPanel.IsVisible = ReferenceEquals(item, NavMods);
        AccountsPanel.IsVisible = ReferenceEquals(item, NavAccounts);
        SettingsPanel.IsVisible = ReferenceEquals(item, NavSettings);

        if (ReferenceEquals(item, NavMods)) RefreshMods();
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLangEvent) return;
        var idx = LanguageCombo.SelectedIndex;
        if (idx < 0 || idx >= LanguageInfo.All.Length) return;

        var lang = LanguageInfo.All[idx];
        Loc.SetLanguage(lang); // fires Loc.Changed -> ApplyLanguage
        _ = SafeAsync(async () =>
        {
            var s = await _core.Settings.LoadAsync();
            s.Language = LanguageInfo.Code(lang);
            await _core.Settings.SaveAsync(s);
        });
    }

    private void ApplySavedLanguage(string code)
    {
        var lang = LanguageInfo.FromCode(code);
        _suppressLangEvent = true;
        LanguageCombo.SelectedIndex = Array.IndexOf(LanguageInfo.All, lang);
        _suppressLangEvent = false;
        Loc.SetLanguage(lang); // no-op (+ no re-apply) if already the current language
    }

    /// <summary>Flushes buffered log lines and progress to the UI, on the UI thread.</summary>
    private void OnUiTick(object? sender, EventArgs e)
    {
        string[] logBatch;
        double? prog;
        string? status;
        lock (_uiLock)
        {
            logBatch = _pendingLog.Count > 0 ? _pendingLog.ToArray() : Array.Empty<string>();
            _pendingLog.Clear();
            prog = _pendingProgress; _pendingProgress = null;
            status = _pendingStatus; _pendingStatus = null;
        }

        if (prog is { } p) DownloadProgress.Value = p;
        if (status != null) PlayStatus.Text = status;

        if (logBatch.Length > 0)
        {
            foreach (var line in logBatch) _logLines.AddLast(line);
            while (_logLines.Count > MaxLogLines) _logLines.RemoveFirst();
            LogBox.Text = string.Join(Environment.NewLine, _logLines);
            LogBox.CaretIndex = LogBox.Text.Length;
        }
    }

    private async Task InitAsync()
    {
        // Restore the saved UI language before anything else so first paint is localized.
        var settings = await _core.Settings.LoadAsync();
        ApplySavedLanguage(settings.Language);

        // Apply the saved default Java and scan the machine for runtimes.
        _core.Game.DefaultJavaPath = settings.DefaultJavaPath;
        await DetectJavaAsync();

        await LoadVersionsAsync();
        await RefreshInstancesAsync();
        await RefreshProfilesAsync();
        // Restore a cached Microsoft session silently, if any.
        _msSession = await _core.Auth.TryResumeMicrosoftAsync();
        if (_msSession != null)
        {
            AccountStatus.Text = Loc.T("account.ms", _msSession.Username);
            RefreshPlayAccountList();   // show the resumed Microsoft account in the profile list
        }

        await CheckForUpdatesAsync();
    }

    // ============================ AUTO-UPDATE ============================

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            _pendingUpdate = await _core.Updates.CheckAsync(
                AppInfo.RepoOwner, AppInfo.RepoName, AppInfo.Version, includeBeta: true);
            RefreshUpdateBanner();
        }
        catch (Exception ex)
        {
            CrashLog.Write("[update] check failed", ex);
        }
    }

    /// <summary>Manual "Check for updates" (About tab): same check, but with visible feedback.</summary>
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateCheckStatus.Text = Loc.T("update.checking");
        try
        {
            _pendingUpdate = await _core.Updates.CheckAsync(
                AppInfo.RepoOwner, AppInfo.RepoName, AppInfo.Version, includeBeta: true);
            RefreshUpdateBanner();
            UpdateCheckStatus.Text = _pendingUpdate == null
                ? Loc.T("update.uptodate")
                : Loc.T(_pendingUpdate.IsBeta ? "update.beta" : "update.stable", _pendingUpdate.Tag);
        }
        catch (Exception ex)
        {
            CrashLog.Write("[update] manual check failed", ex);
            UpdateCheckStatus.Text = Loc.T("update.checkerror");
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    });

    /// <summary>Shows/hides the top banner and localizes its text for the found release.</summary>
    private void RefreshUpdateBanner()
    {
        if (_pendingUpdate == null) { UpdateBanner.IsVisible = false; return; }

        UpdateBanner.IsVisible = true;
        UpdateBannerText.Text = Loc.T(_pendingUpdate.IsBeta ? "update.beta" : "update.stable", _pendingUpdate.Tag);
        UpdateButton.Content = Loc.T(_pendingUpdate.IsBeta ? "btn.installbeta" : "btn.update");
        UpdateDismissButton.Content = Loc.T("btn.later");
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var info = _pendingUpdate;
        if (info == null) return;

        // Stable installs straight away; a beta asks first (it may be unstable).
        if (info.IsBeta)
        {
            var ok = await ConfirmAsync(Loc.T("update.betawarn.title"), Loc.T("update.betawarn.body", info.Tag));
            if (!ok) return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(
                () => UpdateBannerText.Text = Loc.T("update.downloading", (int)(p * 100))));
            var staging = await _core.Updates.DownloadAndExtractAsync(info, progress);

            const string exeName = "FURY Launcher.exe";
            if (!File.Exists(Path.Combine(staging, exeName)))
                throw new InvalidOperationException($"The downloaded update is missing {exeName}.");

            UpdateBannerText.Text = Loc.T("update.installing");
            ApplyUpdateAndRestart(staging, exeName);
        }
        catch (Exception ex)
        {
            CrashLog.Write("[update] install failed", ex);
            UpdateBannerText.Text = Loc.T("update.failed", ex.Message);
            UpdateButton.IsEnabled = true;
        }
    });

    /// <summary>
    /// Writes a tiny batch script that waits for this process to exit, copies the staged
    /// files over the install folder, relaunches, and deletes itself, then shuts down.
    /// </summary>
    private void ApplyUpdateAndRestart(string stagingDir, string exeName)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(appDir, exeName);
        var pid = Environment.ProcessId;
        var bat = Path.Combine(Path.GetTempPath(), "fury-update.bat");

        var script =
            "@echo off\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"robocopy \"{stagingDir}\" \"{appDir}\" /E /IS /IT /NFL /NDL /NJH /NJS /R:3 /W:2 >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            $"rmdir /s /q \"{stagingDir}\" >nul 2>&1\r\n" +
            "del \"%~f0\" >nul 2>&1\r\n";
        File.WriteAllText(bat, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{bat}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    // ============================ PROFILES (offline) ============================

    private async Task RefreshProfilesAsync()
    {
        _profiles = (await _core.Profiles.ListEnsuredAsync()).ToList();
        var names = _profiles.Select(p => p.Name + (p.Slim ? $"  ({Loc.T("model.slim")})" : "")).ToList();

        // Skin tab lists offline profiles only.
        var skinSel = SkinProfileCombo.SelectedIndex;
        SkinProfileCombo.ItemsSource = names.ToList();
        if (_profiles.Count > 0)
            SkinProfileCombo.SelectedIndex = skinSel >= 0 && skinSel < _profiles.Count ? skinSel : 0;

        // Play tab lists offline profiles + the signed-in Microsoft account.
        RefreshPlayAccountList();
    }

    /// <summary>
    /// Fills the Play "Profile" dropdown with the offline profiles and, when signed in,
    /// the Microsoft account (accounts are profiles too). The Microsoft entry is always
    /// last, at index <c>_profiles.Count</c>.
    /// </summary>
    private void RefreshPlayAccountList()
    {
        var items = _profiles.Select(p => p.Name + (p.Slim ? $"  ({Loc.T("model.slim")})" : "")).ToList();
        if (_msSession != null)
            items.Add($"{_msSession.Username}  (Microsoft)");

        var sel = OfflineProfileCombo.SelectedIndex;
        OfflineProfileCombo.ItemsSource = items;
        if (items.Count > 0)
            OfflineProfileCombo.SelectedIndex = sel >= 0 && sel < items.Count ? sel : 0;
    }

    /// <summary>True when the Play dropdown has the Microsoft account entry selected.</summary>
    private bool IsMicrosoftProfileSelected()
        => _msSession != null && OfflineProfileCombo.SelectedIndex == _profiles.Count;

    private OfflineProfile? SelectedSkinProfile()
    {
        var idx = SkinProfileCombo.SelectedIndex;
        return idx >= 0 && idx < _profiles.Count ? _profiles[idx] : null;
    }

    private void SelectProfileById(string id)
    {
        var i = _profiles.FindIndex(p => p.Id == id);
        if (i >= 0) { SkinProfileCombo.SelectedIndex = i; OfflineProfileCombo.SelectedIndex = i; }
    }

    private void OnSkinProfileSelected(object? sender, SelectionChangedEventArgs e)
    {
        var p = SelectedSkinProfile();
        if (p == null) return;
        ProfileNameBox.Text = p.Name;
        SlimCheck.IsChecked = p.Slim;
        SetSkinImages(p.SkinPath);
        SetCapeImage(p.CapePath);
        SkinStatus.Text = Loc.T("skin.profileinfo", p.Name,
            Loc.T(p.SkinPath != null ? "skin.hasskin" : "skin.noskin"),
            Loc.T(p.CapePath != null ? "skin.hascape" : "skin.nocape"),
            Loc.T(p.Slim ? "model.slim" : "model.classic"));
    }

    private async void OnNewProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? Loc.T("profile.newname") : ProfileNameBox.Text!.Trim();
        var p = await _core.Profiles.CreateAsync(name, SlimCheck.IsChecked ?? false);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
        SkinStatus.Text = Loc.T("skin.profilecreated", p.Name);
    });

    private async void OnSaveProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = Loc.T("skin.selectorcreate"); ShowToast(Loc.T("skin.selectorcreate"), error: true); return; }

        var newName = ProfileNameBox.Text?.Trim() ?? "";
        if (newName.Length == 0)
        {
            // The nick is the offline identity; never let a profile be saved without one.
            SkinStatus.Text = Loc.T("skin.nickrequired");
            ShowToast(Loc.T("skin.nickrequired"), error: true);
            ProfileNameBox.Focus();
            return;
        }
        var nameChanged = !string.Equals(newName, p.Name, StringComparison.Ordinal);

        // Warn (once) that changing an offline nick changes the identity/UUID and can lose
        // server progress. Skin/cape stay on the profile; this is only about the nick.
        if (nameChanged)
        {
            var settings = await _core.Settings.LoadAsync();
            if (!settings.SuppressNickChangeWarning)
            {
                var (proceed, dontShow) = await WarnAckAsync(
                    Loc.T("warn.nicktitle"),
                    Loc.T("warn.nickmsg", p.Name, newName),
                    Loc.T("warn.nickack"));
                if (!proceed) { SkinStatus.Text = Loc.T("skin.nickcancelled"); return; }
                if (dontShow)
                {
                    settings.SuppressNickChangeWarning = true;
                    await _core.Settings.SaveAsync(settings);
                }
            }
        }

        p.Name = newName;
        p.Slim = SlimCheck.IsChecked ?? false;
        await _core.Profiles.UpdateAsync(p);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
        SkinStatus.Text = Loc.T("skin.profilesaved", p.Name, Loc.T(p.Slim ? "model.slim" : "model.classic"));
    });

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) return;
        await _core.Profiles.DeleteAsync(p.Id);
        await RefreshProfilesAsync();
        SkinStatus.Text = Loc.T("skin.profiledeleted", p.Name);
    });

    // ============================ INSTANCES ============================

    private async Task RefreshInstancesAsync()
    {
        _instances = (await _core.Instances.ListAsync()).ToList();
        var labels = _instances.Select(i => $"{i.Name}  [{i.McVersion} / {i.Loader}]").ToList();

        // The Home grid binds to the Instance objects (card template); the hidden combos
        // stay label-based so their existing selection logic is unchanged.
        InstancesList.ItemsSource = _instances;
        PlayInstanceCombo.ItemsSource = labels.ToList();
        ModInstanceCombo.ItemsSource = labels.ToList();
        SkinInstanceCombo.ItemsSource = labels.ToList();

        if (_instances.Count > 0)
        {
            if (PlayInstanceCombo.SelectedIndex < 0) PlayInstanceCombo.SelectedIndex = 0;
            if (ModInstanceCombo.SelectedIndex < 0) ModInstanceCombo.SelectedIndex = 0;
            if (SkinInstanceCombo.SelectedIndex < 0) SkinInstanceCombo.SelectedIndex = 0;
            // The Home list is the single selector: default to the first instance.
            if (InstancesList.SelectedIndex < 0) InstancesList.SelectedIndex = 0;
        }

        // Empty-state overlay: prominent "create" call-to-action when there's nothing yet.
        InstancesEmptyState.IsVisible = _instances.Count == 0;
        if (_instances.Count == 0) UpdateHero(null);
    }

    private void OnInstanceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) return;

        _editing = _instances[idx];
        NameBox.Text = _editing.Name;
        SelectVersion(_editing.McVersion);
        LoaderCombo.SelectedIndex = Array.IndexOf(_loaders, _editing.Loader);
        MinRamBox.Text = _editing.MinRamMb.ToString();
        MaxRamBox.Text = _editing.MaxRamMb.ToString();
        JvmArgsBox.Text = _editing.JvmArgs;
        JavaPathBox.Text = _editing.JavaPath ?? "";
        SyncInstanceJavaCombo();
        InstanceStatus.Text = Loc.T("inst.editing", _editing.Name);

        // The instance list is the single selector: keep the (now hidden) per-screen
        // instance combos in sync so play/mods logic is unchanged, and reflect it on Mods.
        SyncSelectedInstance(idx);
        UpdateHero(_editing);
    }

    /// <summary>Refreshes the Home hero band (name + loader · version · mod count) for the selection.</summary>
    private void UpdateHero(Instance? inst)
    {
        if (inst == null)
        {
            HeroName.Text = "—";
            HeroMeta.Text = Loc.T("home.noselection");
            return;
        }
        HeroName.Text = inst.Name;
        int mods;
        try { mods = _core.Mods.ListMods(inst).Count(); }
        catch { mods = 0; }
        HeroMeta.Text = $"{inst.Loader} · {inst.McVersion} · {Loc.T("home.modscount", mods)}";
    }

    /// <summary>Propagates the instance chosen in the Home list to the shared state + hidden combos.</summary>
    private void SyncSelectedInstance(int idx)
    {
        if (PlayInstanceCombo.ItemsSource != null) PlayInstanceCombo.SelectedIndex = idx;
        if (ModInstanceCombo.ItemsSource != null) ModInstanceCombo.SelectedIndex = idx;
        if (SkinInstanceCombo.ItemsSource != null) SkinInstanceCombo.SelectedIndex = idx;

        var inst = idx >= 0 && idx < _instances.Count ? _instances[idx] : null;
        _selected.Current = inst;
        ModsInstanceName.Text = inst?.Name ?? "—";
        SkinInstanceName.Text = inst?.Name ?? "—";

        // Follow the selection: show this instance's log session and reflect whether
        // it is currently running (it may have been launched in the background).
        ShowLogFor(inst?.Id ?? LogHub.GeneralId);
        var running = inst != null && _core.Game.IsRunning(inst.Id);
        StopButton.IsEnabled = running;
        PlayButton.IsEnabled = !running;
        PlayButtonLabel.Text = running ? Loc.T("status.running") : Loc.T("btn.play");

        // Eagerly refresh the mods list so it's already correct when the Mods tab opens.
        RefreshMods();
    }

    private async void OnCreateInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = await _core.Instances.CreateAsync(
            NameBox.Text ?? "", SelectedVersion(), SelectedLoader());
        await RefreshInstancesAsync();
        InstanceOverlay.IsVisible = false;
        InstanceStatus.Text = Loc.T("inst.created", inst.Name, inst.FolderName);
    });

    private async void OnSaveInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (_editing == null) { InstanceStatus.Text = Loc.T("inst.selecttoedit"); return; }

        _editing.Name = (NameBox.Text ?? "").Trim();
        _editing.McVersion = SelectedVersion();
        _editing.Loader = SelectedLoader();
        _editing.MinRamMb = ParseInt(MinRamBox.Text, 512);
        _editing.MaxRamMb = ParseInt(MaxRamBox.Text, 2048);
        _editing.JvmArgs = JvmArgsBox.Text ?? "";
        _editing.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text!.Trim();

        await _core.Instances.UpdateAsync(_editing);
        await RefreshInstancesAsync();
        InstanceOverlay.IsVisible = false;
        InstanceStatus.Text = Loc.T("inst.saved", _editing.Name);
    });

    private async void OnDeleteInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { InstanceStatus.Text = Loc.T("inst.nothingselected"); return; }

        var inst = _instances[idx];
        await _core.Instances.DeleteAsync(inst.Id);
        _editing = null;
        await RefreshInstancesAsync();
        InstanceStatus.Text = Loc.T("inst.deleted", inst.Name);
    });

    private async void OnBrowseJava(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("picker.java"),
            AllowMultiple = false
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) JavaPathBox.Text = path;
    });

    /// <summary>Rescans the machine for Java runtimes and repopulates both pickers.</summary>
    private async Task DetectJavaAsync()
    {
        JavaStatus.Text = Loc.T("java.scanning");
        _javaRuntimes = await Task.Run(JavaLocator.DiscoverAll);
        RebuildJavaCombos();

        JavaStatus.Text = _javaRuntimes.Count > 0
            ? Loc.T("java.found", _javaRuntimes.Count)
            : Loc.T("java.none");

        var stub = JavaLocator.OracleStubPath();
        JavaOracleWarn.IsVisible = stub != null;
        JavaOracleWarn.Text = stub != null ? Loc.T("java.oraclewarn") : "";
    }

    /// <summary>(Re)fills both Java combos from the cached runtimes; index 0 is "Auto".</summary>
    private void RebuildJavaCombos()
    {
        var items = new List<string> { Loc.T("java.auto") };
        foreach (var r in _javaRuntimes) items.Add(r.Display);

        _suppressJavaEvents = true;
        DefaultJavaCombo.ItemsSource = items;
        JavaCombo.ItemsSource = new List<string>(items);

        // Reselect the saved default (Auto when unset or no longer present).
        var def = _core.Game.DefaultJavaPath;
        var i = string.IsNullOrEmpty(def) ? -1 : IndexOfRuntime(def);
        DefaultJavaCombo.SelectedIndex = i >= 0 ? i + 1 : 0;
        SyncJavaComboToBox();
        _suppressJavaEvents = false;
    }

    /// <summary>Points the instance Java combo at whatever JavaPathBox holds (suppressed-safe).</summary>
    private void SyncInstanceJavaCombo()
    {
        _suppressJavaEvents = true;
        SyncJavaComboToBox();
        _suppressJavaEvents = false;
    }

    private void SyncJavaComboToBox()
    {
        var path = JavaPathBox.Text;
        if (string.IsNullOrWhiteSpace(path)) { JavaCombo.SelectedIndex = 0; return; } // Auto
        var i = IndexOfRuntime(path.Trim());
        JavaCombo.SelectedIndex = i >= 0 ? i + 1 : -1; // custom path not in the list → blank
    }

    private int IndexOfRuntime(string path)
    {
        for (int i = 0; i < _javaRuntimes.Count; i++)
            if (string.Equals(_javaRuntimes[i].Path, path, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private async void OnDefaultJavaChanged(object? sender, SelectionChangedEventArgs e) => await SafeAsync(async () =>
    {
        if (_suppressJavaEvents) return;
        var idx = DefaultJavaCombo.SelectedIndex;
        string? path = idx > 0 && idx - 1 < _javaRuntimes.Count ? _javaRuntimes[idx - 1].Path : null;

        _core.Game.DefaultJavaPath = path;
        var s = await _core.Settings.LoadAsync();
        s.DefaultJavaPath = path;
        await _core.Settings.SaveAsync(s);
        JavaStatus.Text = Loc.T("java.defaultsaved");
    });

    private void OnInstanceJavaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressJavaEvents) return;
        var idx = JavaCombo.SelectedIndex;
        if (idx == 0) { JavaPathBox.Text = ""; return; }          // Auto
        if (idx > 0 && idx - 1 < _javaRuntimes.Count)             // a detected runtime
            JavaPathBox.Text = _javaRuntimes[idx - 1].Path;
    }

    private LoaderType SelectedLoader()
    {
        var i = LoaderCombo.SelectedIndex;
        return i >= 0 && i < _loaders.Length ? _loaders[i] : LoaderType.Vanilla;
    }

    private string SelectedVersion() => (McVersionCombo.SelectedItem as string ?? "").Trim();

    /// <summary>Selects a version in the dropdown, adding it first if it isn't in the list
    /// (e.g. a snapshot or an old version an existing instance was created with).</summary>
    private void SelectVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        if (!_versions.Contains(version))
        {
            _versions.Insert(0, version);
            McVersionCombo.ItemsSource = null;
            McVersionCombo.ItemsSource = _versions;
        }
        McVersionCombo.SelectedItem = version;
    }

    /// <summary>Fills the version dropdown from Mojang's manifest, with an offline fallback.</summary>
    private async Task LoadVersionsAsync()
    {
        // Seed a small fallback list so the dropdown works even with no network.
        _versions = new List<string> { "1.21.1", "1.20.1", "1.19.4", "1.18.2", "1.16.5", "1.12.2", "1.8.9" };
        McVersionCombo.ItemsSource = _versions;
        McVersionCombo.SelectedIndex = 0;
        try
        {
            var releases = await _core.Versions.GetReleasesAsync();
            if (releases.Count > 0)
            {
                _versions = releases.ToList();
                McVersionCombo.ItemsSource = _versions;
                McVersionCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            AppendLog(Loc.T("log.error") + ex.Message);
        }
    }

    // ============================== PLAY ==============================

    private async void OnMicrosoftLogin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        MicrosoftLoginButton.IsEnabled = false;
        AccountStatus.Text = Loc.T("ms.opening");
        try
        {
            _msSession = await _core.Auth.LoginMicrosoftAsync();
            AccountStatus.Text = Loc.T("account.ms", _msSession.Username);
            RefreshPlayAccountList();
            OfflineProfileCombo.SelectedIndex = _profiles.Count; // select the Microsoft entry
        }
        finally
        {
            MicrosoftLoginButton.IsEnabled = true;
        }
    });

    private async void OnMicrosoftLogout(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        await _core.Auth.SignOutMicrosoftAsync();
        _msSession = null;
        RefreshPlayAccountList();
        OfflineProfileCombo.SelectedIndex = _profiles.Count > 0 ? 0 : -1;   // back to an offline profile
        AccountStatus.Text = Loc.T("status.offlineparen");
    });

    /// <summary>
    /// Called by the auth flow (possibly off the UI thread): shows a dialog asking the
    /// user to paste the URL the browser landed on after signing in. Returns the pasted
    /// text, or null if cancelled.
    /// </summary>
    private Task<string?> PromptForAuthCodeAsync(Uri authUrl, CancellationToken ct)
    {
        var outer = new TaskCompletionSource<string?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { outer.TrySetResult(await ShowAuthPasteDialogAsync(authUrl)); }
            catch (Exception ex) { outer.TrySetException(ex); }
        });
        return outer.Task;
    }

    private async Task<string?> ShowAuthPasteDialogAsync(Uri authUrl)
    {
        var tcs = new TaskCompletionSource<string?>();
        var box = new TextBox { Watermark = Loc.T("auth.paste.watermark"), Width = 480 };
        var ok = new Button { Content = Loc.T("btn.continue") };
        var cancel = new Button { Content = Loc.T("btn.cancel") };
        var reopen = new Button { Content = Loc.T("auth.reopen") };

        var dialog = new Window
        {
            Title = Loc.T("auth.paste.title"),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Loc.T("auth.paste.body"), TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { reopen, ok, cancel }
                    }
                }
            }
        };

        reopen.Click += (_, _) => OpenUrlSafe(authUrl.ToString());
        ok.Click += (_, _) => { tcs.TrySetResult(box.Text); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static void OpenUrlSafe(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("[auth] reopen browser failed", ex); }
    }

    private async void OnPlay(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = PlayInstanceCombo.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { PlayStatus.Text = Loc.T("common.selectinstance"); return; }
        var inst = _instances[idx];

        // Show this instance's log for the launch, even if it differs from the selection.
        ShowLogFor(inst.Id);

        MSession session;
        OfflineProfile? offlineProfile = null;
        if (IsMicrosoftProfileSelected())
        {
            session = _msSession!;   // the Microsoft account profile is selected
        }
        else
        {
            var pIdx = OfflineProfileCombo.SelectedIndex;
            offlineProfile = pIdx >= 0 && pIdx < _profiles.Count ? _profiles[pIdx] : null;
            // The selected offline profile's name IS the nick.
            session = _core.Auth.CreateOffline(offlineProfile?.Name ?? "Player");
        }

        _launchCts = new CancellationTokenSource();
        PlayButton.IsEnabled = false;
        PlayButtonLabel.Text = Loc.T("play.preparing");
        DownloadProgress.Value = 0;
        PlayStatus.Text = Loc.T("play.preparing");
        try
        {
            // Auto-apply the active profile's skin/cape (offline) so the user never has to
            // "apply by name": it just follows the profile, even after a rename.
            if (offlineProfile != null && inst.Loader != LoaderType.Vanilla &&
                (offlineProfile.SkinPath != null || offlineProfile.CapePath != null))
            {
                try
                {
                    await _core.Skins.ApplyOfflineAsync(inst, offlineProfile,
                        new Progress<string>(AppendLog), _launchCts.Token);
                }
                catch (Exception ex)
                {
                    AppendLog(Loc.T("log.skinnotapplied") + ex.Message);
                }
            }

            await _core.Game.LaunchAsync(inst, session, _launchCts.Token);
        }
        catch (OperationCanceledException)
        {
            PlayStatus.Text = Loc.T("status.cancelled");
            PlayButton.IsEnabled = true;
            PlayButtonLabel.Text = Loc.T("btn.play");
        }
        catch (Exception ex)
        {
            CrashLog.Write("[play] launch failed", ex);
            AppendLog(Loc.T("log.error") + ex.Message);
            PlayStatus.Text = Loc.T("status.error", ex.Message);
            LogDrawer.IsExpanded = true;
            PlayButton.IsEnabled = true;
            PlayButtonLabel.Text = Loc.T("btn.play");
        }
    });

    /// <summary>
    /// Writes a UI-side log line (auth, mods, skins, errors) to the session currently
    /// shown. Game output is written to its own instance session by the Core instead.
    /// Thread-safe: the session marshals the line onto the UI render timer.
    /// </summary>
    private void AppendLog(string line)
        => (_displayedSession ?? _core.Logs.Get(LogHub.GeneralId)).Append(line);

    /// <summary>
    /// Points the log panel at one instance's session: detaches the old session,
    /// reloads the view from the new session's history, and streams its new lines.
    /// Runs on the UI thread (touches LogBox).
    /// </summary>
    private void ShowLogFor(string? instanceId)
    {
        var session = _core.Logs.Get(instanceId);
        if (ReferenceEquals(session, _displayedSession)) return;

        if (_displayedSession != null && _sessionSink != null)
            _displayedSession.LineAdded -= _sessionSink;

        _displayedSession = session;
        _sessionSink = line => { lock (_uiLock) _pendingLog.Enqueue(line); };
        session.LineAdded += _sessionSink;

        // Drop any pending lines from the previous session and rebuild from history.
        lock (_uiLock) _pendingLog.Clear();
        _logLines.Clear();
        foreach (var l in session.Snapshot()) _logLines.AddLast(l);
        while (_logLines.Count > MaxLogLines) _logLines.RemoveFirst();
        LogBox.Text = string.Join(Environment.NewLine, _logLines);
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    /// <summary>Reflects a running/stopped state on the Play/Stop controls.</summary>
    private void ApplyRunState(bool running)
    {
        StopButton.IsEnabled = running;
        PlayButton.IsEnabled = !running;
        PlayButtonLabel.Text = running ? Loc.T("status.running") : Loc.T("btn.play");
        PlayStatus.Text = running ? Loc.T("status.running") : Loc.T("status.ended");
    }

    // =============================== MODS ===============================

    private Instance? SelectedModInstance()
    {
        var idx = ModInstanceCombo.SelectedIndex;
        return idx >= 0 && idx < _instances.Count ? _instances[idx] : null;
    }

    private void RefreshMods()
    {
        UpdateModsAvailability();
        UpdateContentTitle();

        var inst = SelectedModInstance();
        ModsInstanceName.Text = inst?.Name ?? "—";

        _modEnrichCts?.Cancel();
        _modVms.Clear();
        _allModVms.Clear();
        if (inst == null)
        {
            _mods = new();
            ModsList.ItemsSource = null;
            ModsEmptyState.IsVisible = true;
            ModsCountBadge.Text = "0";
            return;
        }

        _mods = _core.Mods.ListContent(inst, _contentKind).ToList();
        foreach (var m in _mods)
            _allModVms.Add(new InstalledModVm(m) { ToggleRequested = OnModToggleRequested });
        ModsList.ItemsSource = _modVms;
        ApplyModFilter();
        ModsEmptyState.IsVisible = _mods.Count == 0;
        ModsCountBadge.Text = _mods.Count.ToString();

        // Resolve real names/icons off-thread (Modrinth hash lookup works for zips too),
        // and refresh the "already installed" set for the search comparator.
        _modEnrichCts = new CancellationTokenSource();
        _ = EnrichModsAsync(inst, _allModVms.ToList(), _modEnrichCts.Token);
        _ = RefreshInstalledSignaturesAsync(inst);
    }

    /// <summary>Sets the installed-list header to the selected content type.</summary>
    private void UpdateContentTitle()
        => LblInstalledMods.Text = _contentKind switch
        {
            ContentKind.Shader => "Shaders",
            ContentKind.Datapack => "Datapacks",
            _ => "Mods"
        };

    /// <summary>Switches the content type (Mods / Shaders / Datapacks): re-lists and re-searches.</summary>
    private void OnContentSegmentChanged(object? sender, RoutedEventArgs e)
    {
        var kind = SegShaders.IsChecked == true ? ContentKind.Shader
                 : SegDatapacks.IsChecked == true ? ContentKind.Datapack
                 : ContentKind.Mod;
        if (kind == _contentKind) return;
        _contentKind = kind;
        RefreshMods();
        StartModrinthSearch();
    }

    /// <summary>Switches the search source (Modrinth / CurseForge) and re-runs the search.</summary>
    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        var source = SrcCurseForge.IsChecked == true ? ContentSource.CurseForge : ContentSource.Modrinth;
        if (source == _contentSource) return;
        _contentSource = source;
        ModrinthQueryBox.Watermark = Loc.T("watermark.search", SourceName());
        StartModrinthSearch();
    }

    /// <summary>Display name of the active search source, derived from state — never a
    /// provider name hardcoded into an individual message.</summary>
    private string SourceName() => _contentSource == ContentSource.CurseForge ? "CurseForge" : "Modrinth";

    /// <summary>Plays a one-shot fade-in on a freshly realized mod card (no replay on interaction).</summary>
    private static void OnModContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0d) }
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1d) }
                }
            }
        };
        _ = anim.RunAsync(e.Container);
    }

    /// <summary>Filters the shown mods by the header search box (name or file name). No reload of the source.</summary>
    private void ApplyModFilter()
    {
        var query = (ModsFilterBox.Text ?? "").Trim();
        _modVms.Clear();
        foreach (var vm in _allModVms)
        {
            if (query.Length == 0
                || vm.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || vm.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                _modVms.Add(vm);
        }
    }

    /// <summary>Recomputes what the instance already has and re-flags the visible search results.</summary>
    private async Task RefreshInstalledSignaturesAsync(Instance inst)
    {
        try
        {
            _installedSigs = await _core.ModMetadata.GetInstalledSignaturesAsync(inst, _contentKind);
            MarkInstalledResults();
        }
        catch (Exception ex) { CrashLog.Write("[mods] computing installed signatures failed", ex); }
    }

    /// <summary>
    /// Common-sense guard against installing two incompatible equivalents (Sodium/Embeddium,
    /// Iris/Oculus, ...). Returns true to proceed. On a detected conflict, asks the user to
    /// cancel or replace; "replace" removes the installed equivalent first.
    /// </summary>
    private async Task<bool> EnsureNoConflictAsync(Instance inst, ModrinthHitVm vm)
    {
        var sigs = _installedSigs ?? await _core.ModMetadata.GetInstalledSignaturesAsync(inst, _contentKind);
        var conflict = ModConflicts.Find(vm.Hit.Slug, vm.Title, sigs.Names);
        if (conflict == null) return true;

        static string Pretty(string k) => k.Length == 0 ? k : char.ToUpperInvariant(k[0]) + k[1..];
        var installedName = Pretty(conflict.Value.installed);
        var incomingName = string.IsNullOrWhiteSpace(vm.Title) ? Pretty(conflict.Value.incoming) : vm.Title;

        var replace = await ConfirmAsync(
            Loc.T("conflict.title"),
            Loc.T("conflict.message", incomingName, installedName),
            Loc.T("btn.replace"), Loc.T("btn.cancel"));
        if (!replace) return false;

        await _core.Mods.RemoveByKeywordAsync(inst, conflict.Value.installed, _contentKind);
        RefreshMods();
        return true;
    }

    /// <summary>Tags each Modrinth result the instance already has (by project id or name).</summary>
    private void MarkInstalledResults()
    {
        var sigs = _installedSigs;
        if (sigs == null) return;
        foreach (var vm in _modrinthVms)
            if (!vm.Installing)
                vm.Installed = sigs.IsInstalled(vm.ProjectId, vm.Title, vm.Hit.Slug);
    }

    /// <summary>Fills each installed-mod card with its resolved name, version and icon.</summary>
    private async Task EnrichModsAsync(Instance inst, List<InstalledModVm> vms, CancellationToken ct)
    {
        try
        {
            foreach (var vm in vms)
            {
                if (ct.IsCancellationRequested) return;
                // Resolves from the index, or identifies the jar on Modrinth by hash and
                // caches it, so manual/CurseForge mods get a real name, version and icon.
                var info = await _core.ModMetadata.ResolveWithOnlineAsync(inst, vm.Item, _contentKind, ct);
                Bitmap? icon = null;
                if (info.IconPath is { } iconPath)
                    icon = await Task.Run(() => TryLoadBitmap(iconPath), ct);
                if (ct.IsCancellationRequested) return;
                vm.Apply(info.Title, info.Version, info.Description, icon);
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh */ }
        catch (Exception ex) { CrashLog.Write("[mods] enriching metadata failed", ex); }
    }

    private static Bitmap? TryLoadBitmap(string path)
    {
        try { return new Bitmap(path); }
        catch { return null; } // unsupported format (e.g. webp) → keep the placeholder
    }

    /// <summary>
    /// Mods can't go into a Vanilla instance. Rather than let the install fail into the
    /// log (where users missed it), we disable the install controls, show a clear inline
    /// notice, and offer a shortcut to create a modded instance.
    /// </summary>
    private void UpdateModsAvailability()
    {
        var inst = SelectedModInstance();
        // Only mods need a loader; shaders/datapacks are fine on a Vanilla instance.
        var isVanilla = inst != null && inst.Loader == LoaderType.Vanilla && _contentKind == ContentKind.Mod;

        ModsVanillaPanel.IsVisible = isVanilla;
        AddModButton.IsEnabled = !isVanilla;
        ModrinthDownloadButton.IsEnabled = !isVanilla;

        var tip = isVanilla ? Loc.T("mods.vanillatip") : null;
        ToolTip.SetTip(AddModButton, tip);
        ToolTip.SetTip(ModrinthDownloadButton, tip);
    }

    /// <summary>Empty-state / Vanilla shortcut: jump to the Instances tab ready to create one.</summary>
    private void OnEmptyStateNew(object? sender, RoutedEventArgs e) => StartNewInstanceFlow();

    private void OnCreateModdedInstance(object? sender, RoutedEventArgs e) => StartNewInstanceFlow();

    private void StartNewInstanceFlow()
    {
        NavView.SelectedItem = NavHome;
        OpenInstanceDialog(isNew: true);
    }

    /// <summary>Shows the create/edit overlay. New mode clears the form; edit mode uses the current selection.</summary>
    private void OpenInstanceDialog(bool isNew)
    {
        if (isNew)
        {
            _editing = null;
            NameBox.Text = "";
            // Default the loader to a modded one so "create" is immediately mod-capable.
            if (LoaderCombo.SelectedIndex <= 0 && _loaders.Length > 1)
                LoaderCombo.SelectedIndex = Array.IndexOf(_loaders, LoaderType.Fabric);
            MinRamBox.Text = "512";
            MaxRamBox.Text = "2048";
            JvmArgsBox.Text = "";
            JavaPathBox.Text = "";
            SyncInstanceJavaCombo();
            InstanceStatus.Text = "";
            OverlayTitle.Text = Loc.T("home.newtitle");
        }
        else
        {
            // The form is kept in sync with the selection by OnInstanceSelected.
            if (_editing == null) { Notify(Loc.T("inst.selecttoedit")); return; }
            OverlayTitle.Text = Loc.T("home.edittitle");
        }
        NewInstanceButton.IsVisible = isNew;
        SaveInstanceButton.IsVisible = !isNew;
        InstanceOverlay.IsVisible = true;
        NameBox.Focus();
    }

    /// <summary>Double-clicking an instance card launches it directly.</summary>
    private void OnInstanceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (InstancesList.SelectedIndex < 0) return;
        OnPlay(sender, new RoutedEventArgs());
    }

    private async void OnOpenInstanceFolder(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { Notify(Loc.T("inst.nothingselected")); return; }
        var dir = _core.Paths.InstanceDir(_instances[idx]);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        await Task.CompletedTask;
    });

    private async void OnAddMod(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        if (inst == null) { Notify(Loc.T("mods.selectinstancetab")); return; }

        // Mods are .jar; shaders/datapacks are .zip.
        var pattern = _contentKind == ContentKind.Mod ? "*.jar" : "*.zip";
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("picker.mod"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(pattern) { Patterns = new[] { pattern } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await _core.Mods.AddContentAsync(inst, path, _contentKind);
        RefreshMods();
        Notify(Loc.T("mods.added", System.IO.Path.GetFileName(path)));
    });

    /// <summary>Per-card remove: deletes the jar and drops just that card (no list reload).</summary>
    private async void OnRemoveModClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        if (inst == null || sender is not Control { DataContext: InstalledModVm vm }) return;

        await _core.Mods.RemoveContentAsync(inst, vm.FileName, _contentKind);
        _allModVms.Remove(vm);
        _modVms.Remove(vm); // in-place removal; the ItemsControl drops only this card
        ModsCountBadge.Text = _allModVms.Count.ToString();
        ModsEmptyState.IsVisible = _allModVms.Count == 0;
        await RefreshInstalledSignaturesAsync(inst); // search results un-mark this item
        Notify(Loc.T("mods.removed"));
    });

    /// <summary>
    /// Runs from the item VM's Enabled setter when the switch flips: renames the jar in
    /// place and never reloads the list, so the ToggleSwitch keeps its native animation.
    /// </summary>
    private void OnModToggleRequested(InstalledModVm vm, bool enabled)
    {
        var inst = SelectedModInstance();
        if (inst == null) return;

        // Defer the jar rename so the switch's slide animation isn't blocked by disk I/O.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                vm.UpdateFileName(_core.Mods.ToggleMod(inst, vm.FileName, _contentKind));
            }
            catch (Exception ex)
            {
                CrashLog.Write("[mods] toggle failed", ex);
                vm.SetEnabledSilently(!enabled); // revert the switch on failure
                Notify(Loc.T("status.error", ex.Message));
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Begins a fresh Modrinth search: cancels the previous one, clears results, loads page 0.</summary>
    private void StartModrinthSearch()
    {
        var inst = SelectedModInstance();
        if (inst == null) { ModrinthStatus.Text = Loc.T("common.selectinstance"); return; }

        _modrinthCts?.Cancel();
        _modrinthCts = new CancellationTokenSource();
        _modrinthQuery = ModrinthQueryBox.Text ?? "";
        _modrinthOffset = 0;
        _modrinthHasMore = true;
        _modrinthVms.Clear();
        _modVersions = new List<ModrinthVersion>();
        ModrinthVersionCombo.ItemsSource = null;
        ModrinthEmptyState.IsVisible = false;
        _ = LoadNextModrinthPageAsync(_modrinthCts.Token);
    }

    /// <summary>Loads the next page of results and appends it; icons stream in asynchronously.</summary>
    private async Task LoadNextModrinthPageAsync(CancellationToken ct)
    {
        if (_modrinthLoading || !_modrinthHasMore) return;
        var inst = SelectedModInstance();
        if (inst == null) return;

        _modrinthLoading = true;
        ModrinthStatus.Text = Loc.T("mods.searching", SourceName());
        try
        {
            var page = await _core.Mods.SearchContentAsync(inst, _modrinthQuery, _contentSource, _contentKind, _modrinthOffset, ct);
            if (ct.IsCancellationRequested) return;

            foreach (var hit in page)
            {
                var vm = new ModrinthHitVm(hit);
                _modrinthVms.Add(vm);
                _ = LoadIconAsync(vm, ct);
            }
            MarkInstalledResults(); // flag any results the instance already has
            _modrinthOffset += page.Count;
            if (page.Count < ModrinthClient.SearchPageSize) _modrinthHasMore = false;

            ModrinthEmptyState.IsVisible = _modrinthVms.Count == 0;
            // Only a genuine empty result reaches here — errors throw and are shown distinctly.
            ModrinthStatus.Text = _modrinthVms.Count == 0
                ? Loc.T("mods.noresults", SourceName())
                : Loc.T("mods.results", _modrinthVms.Count);
        }
        catch (OperationCanceledException) { /* superseded by a newer search */ }
        catch (ContentSourceException cse)
        {
            CrashLog.Write("[content] search failed", cse);
            ModrinthStatus.Text = cse.Kind switch
            {
                ContentSourceErrorKind.Auth => Loc.T("mods.err.auth", cse.Source),
                ContentSourceErrorKind.Network => Loc.T("mods.err.network"),
                _ => Loc.T("status.error", cse.Message)
            };
        }
        catch (HttpRequestException hre)
        {
            // No StatusCode => the request never reached the server (no network / DNS / TLS).
            ModrinthStatus.Text = hre.StatusCode is null
                ? Loc.T("mods.err.network")
                : Loc.T("status.error", $"HTTP {(int)hre.StatusCode}");
        }
        catch (Exception ex)
        {
            CrashLog.Write("[content] search failed", ex);
            ModrinthStatus.Text = Loc.T("status.error", ex.Message);
        }
        finally { _modrinthLoading = false; }
    }

    /// <summary>Pages in more results when the list is scrolled near the bottom.</summary>
    private void OnModrinthScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var sv = lb.FindDescendantOfType<ScrollViewer>();
        if (sv == null || _modrinthLoading || !_modrinthHasMore || _modrinthCts == null) return;
        // Within ~1.5 viewports of the end → prefetch the next page.
        if (sv.Offset.Y + sv.Viewport.Height * 1.5 >= sv.Extent.Height)
            _ = LoadNextModrinthPageAsync(_modrinthCts.Token);
    }

    /// <summary>Downloads a hit's icon (cached by URL) and assigns it on the UI thread.</summary>
    private async Task LoadIconAsync(ModrinthHitVm vm, CancellationToken ct)
    {
        var url = vm.IconUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (_iconCache.TryGetValue(url, out var cached)) { vm.Icon = cached; return; }

        try { await _iconGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            // Re-check: another card with the same icon may have fetched it while we waited.
            if (_iconCache.TryGetValue(url, out var now)) { await Dispatcher.UIThread.InvokeAsync(() => vm.Icon = now); return; }

            var bytes = await _iconHttp.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _iconCache[url] = bmp;
                vm.Icon = bmp;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer search */ }
        catch { /* icon is decorative; a missing one just keeps the placeholder */ }
        finally { _iconGate.Release(); }
    }

    /// <summary>Per-card one-click install: newest compatible version + required dependencies.</summary>
    private async void OnModrinthInstallClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (sender is not Control c || c.DataContext is not ModrinthHitVm vm) return;
        var inst = SelectedModInstance();
        if (inst == null) { Notify(Loc.T("common.selectinstance")); return; }
        if (!await EnsureNoConflictAsync(inst, vm)) return;

        vm.Installing = true;
        ModrinthStatus.Text = Loc.T("mods.downloading", vm.Title);
        try
        {
            var installed = await _core.Mods.InstallProjectFromSourceAsync(inst, vm.ProjectId, _contentSource, _contentKind);
            RefreshMods();
            ModrinthStatus.Text = Loc.T("mods.installedcount", installed.Count);
            vm.Installed = true; // card shows a check instead of the button
        }
        finally { vm.Installing = false; }
    });

    /// <summary>When a result is picked, load its versions into the advanced version chooser.</summary>
    private async void OnModrinthResultSelected(object? sender, SelectionChangedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        var idx = ModrinthList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _modrinthVms.Count)
        {
            _modVersions = new List<ModrinthVersion>();
            ModrinthVersionCombo.ItemsSource = null;
            return;
        }

        _modVersions = await _core.Mods.GetContentVersionsAsync(inst, _modrinthVms[idx].ProjectId, _contentSource, _contentKind);
        ModrinthVersionCombo.ItemsSource = _modVersions.Select(v => v.Display).ToList();
        if (_modVersions.Count > 0) ModrinthVersionCombo.SelectedIndex = 0;
    });

    private async void OnModrinthDownload(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        var vIdx = ModrinthVersionCombo.SelectedIndex;
        if (inst == null || vIdx < 0 || vIdx >= _modVersions.Count) { Notify(Loc.T("mods.selectresult")); return; }

        if (ModrinthList.SelectedItem is ModrinthHitVm selVm && !await EnsureNoConflictAsync(inst, selVm)) return;

        var version = _modVersions[vIdx];
        ModrinthStatus.Text = Loc.T("mods.downloading", version.Display);
        var log = new Progress<string>(f => AppendLog("[mod] " + f));
        var installed = await _core.Mods.InstallVersionFromSourceAsync(inst, version, _contentSource, _contentKind, log);
        RefreshMods();
        ModrinthStatus.Text = Loc.T("mods.installedcount", installed.Count);
    });

    // ============================ MODPACK (.frpack) ============================

    private async void OnExportPack(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { InstanceStatus.Text = Loc.T("pack.selecttoexport"); return; }
        var inst = _instances[idx];

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("pack.exporttitle"),
            SuggestedFileName = inst.Name + ".frpack",
            DefaultExtension = "frpack",
            FileTypeChoices = new[] { new FilePickerFileType(Loc.T("filetype.frpack")) { Patterns = new[] { "*.frpack" } } }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        InstanceStatus.Text = Loc.T("pack.exporting");
        await _core.Packs.ExportAsync(inst, path);
        InstanceStatus.Text = Loc.T("pack.exported", path);
    });

    private async void OnImportPack(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("pack.importtitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Loc.T("filetype.frpack")) { Patterns = new[] { "*.frpack" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        // Validate first and warn before importing if anything looks off.
        var preview = await _core.Packs.ReadManifestAsync(path);
        if (preview.Warnings.Count > 0)
        {
            var ok = await ConfirmAsync(Loc.T("pack.warntitle"),
                string.Join(Environment.NewLine, preview.Warnings) +
                Environment.NewLine + Environment.NewLine + Loc.T("pack.warnsuffix"));
            if (!ok) { InstanceStatus.Text = Loc.T("pack.importcancelled"); return; }
        }

        InstanceStatus.Text = Loc.T("pack.importing");
        var inst = await _core.Packs.ImportAsync(path);
        await RefreshInstancesAsync();
        InstanceStatus.Text = Loc.T("pack.imported", inst.Name, inst.McVersion, inst.Loader);
    });

    private async void OnImportMrpack(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("pack.importtitle"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Modrinth modpack") { Patterns = new[] { "*.mrpack" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        InstanceStatus.Text = Loc.T("mrpack.importing");
        var progress = new Progress<(int done, int total)>(p =>
            InstanceStatus.Text = Loc.T("mrpack.downloading", p.done, p.total));
        var inst = await _core.Mrpacks.ImportAsync(path, progress);
        await RefreshInstancesAsync();
        InstanceStatus.Text = Loc.T("pack.imported", inst.Name, inst.McVersion, inst.Loader);
    });

    // =============================== SKIN / CAPE ===============================

    private async void OnChooseSkin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = Loc.T("skin.createselectfirst"); return; }
        var path = await PickImageAsync(Loc.T("picker.skin"));
        if (path == null) return;
        await _core.Profiles.SetSkinAsync(p, path);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
    });

    private async void OnChooseCape(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = Loc.T("skin.createselectfirst"); return; }
        var path = await PickImageAsync(Loc.T("picker.cape"));
        if (path == null) return;
        await _core.Profiles.SetCapeAsync(p, path);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
    });

    private async void OnApplySkin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = Loc.T("skin.createselect"); return; }
        var idx = SkinInstanceCombo.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { SkinStatus.Text = Loc.T("skin.selectinstanceapply"); return; }
        var inst = _instances[idx];

        ApplySkinButton.IsEnabled = false;
        SkinStatus.Text = Loc.T("skin.applying");
        try
        {
            var log = new Progress<string>(AppendLog);
            await _core.Skins.ApplyOfflineAsync(inst, p, log);
            SkinStatus.Text = Loc.T("skin.applied", p.Name, inst.Name);
        }
        finally
        {
            ApplySkinButton.IsEnabled = true;
        }
    });

    private async Task<string?> PickImageAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Loc.T("filetype.png")) { Patterns = new[] { "*.png" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private void SetSkinImages(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SkinImage.Source = null;
            FaceImage.Source = null;
            return;
        }

        var bmp = new Bitmap(path);
        SkinImage.Source = bmp;

        // Cheap face preview: the 8x8 face region lives at (8,8) in every skin
        // texture (64x64 and legacy 64x32). Nearest-neighbor upscaling keeps it crisp.
        try { FaceImage.Source = new CroppedBitmap(bmp, new PixelRect(8, 8, 8, 8)); }
        catch { FaceImage.Source = null; } // not a real skin texture, skip the crop
    }

    private void SetCapeImage(string? path)
    {
        CapeImage.Source = string.IsNullOrEmpty(path) || !File.Exists(path) ? null : new Bitmap(path);
    }

    // ============================== HELPERS ==============================

    /// <summary>Minimal modal yes/no dialog (bare UI: no MessageBox in Avalonia).</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, string? cancelLabel = null)
    {
        var tcs = new TaskCompletionSource<bool>();
        var yes = new Button { Content = confirmLabel ?? Loc.T("btn.continue") };
        var no = new Button { Content = cancelLabel ?? Loc.T("btn.cancel") };

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { yes, no }
                    }
                }
            }
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    /// <summary>
    /// Modal warning with an acknowledge button and a "don't show again" checkbox.
    /// Returns whether the user proceeded and whether to suppress future warnings.
    /// </summary>
    private async Task<(bool proceed, bool dontShowAgain)> WarnAckAsync(string title, string message, string ackButton)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dontShow = new CheckBox { Content = Loc.T("warn.dontshow") };
        var ack = new Button { Content = ackButton };
        var cancel = new Button { Content = Loc.T("btn.cancel") };

        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    dontShow,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { ack, cancel }
                    }
                }
            }
        };

        ack.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(this);
        var proceed = await tcs.Task;
        return (proceed, dontShow.IsChecked ?? false);
    }

    private void Notify(string message)
    {
        PlayStatus.Text = message;
        AppendLog(Loc.T("log.info") + message);
        ShowToast(message);
    }

    private static readonly IBrush ToastAccentBrush = new SolidColorBrush(Color.Parse("#6D28D9"));
    private static readonly IBrush ToastDangerBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private CancellationTokenSource? _toastCts;

    /// <summary>Shows a brief animated toast (fade + slide) at the bottom of the window.</summary>
    private async void ShowToast(string message, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastText.Text = message;
        ToastBorder.BorderBrush = error ? ToastDangerBrush : ToastAccentBrush;

        // The slide transform is named in XAML but Avalonia only generates fields for
        // controls, not transforms, so reach it through RenderTransform instead.
        var slide = ToastBorder.RenderTransform as TranslateTransform;

        ToastBorder.Opacity = 1;   // transitions animate opacity + slide
        if (slide != null) slide.Y = 0;
        try { await Task.Delay(error ? 4500 : 2600, cts.Token); }
        catch (TaskCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        ToastBorder.Opacity = 0;
        if (slide != null) slide.Y = 24;
    }

    /// <summary>Runs an async handler, surfacing any error to the log (never swallowed).</summary>
    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            CrashLog.Write("[ui] unhandled action error", ex);
            Dispatcher.UIThread.Post(() =>
            {
                AppendLog(Loc.T("log.error") + ex.Message);
                PlayStatus.Text = Loc.T("status.error", ex.Message);
                ShowToast(ex.Message, error: true);
                // Surface the log so the error is never silent.
                if (LogDrawer != null) LogDrawer.IsExpanded = true;
            });
        }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, out var v) && v > 0 ? v : fallback;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
