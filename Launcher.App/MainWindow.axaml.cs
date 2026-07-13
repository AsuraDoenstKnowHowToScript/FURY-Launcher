// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using CmlLib.Core.Auth;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Launcher.App.Services;
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
    private List<OfflineProfile> _profiles = new();
    private IReadOnlyList<ModrinthHit> _hits = new List<ModrinthHit>();
    private IReadOnlyList<ModrinthVersion> _modVersions = new List<ModrinthVersion>();

    private Instance? _editing;      // instance selected for editing (Instances tab)
    private MSession? _msSession;    // cached Microsoft session after login
    private CancellationTokenSource? _launchCts;
    private bool _suppressLangEvent; // guards the language dropdown during programmatic set
    private List<string> _versions = new(); // Minecraft versions shown in the create/edit dropdown
    private UpdateInfo? _pendingUpdate;     // newer release found on GitHub, if any
    private readonly SelectedInstanceService _selected = App.Services.GetRequiredService<SelectedInstanceService>();

    // Coalesced UI updates. The game/installer raise log + progress events far too
    // fast to touch the UI once per event (doing so froze the window into "not
    // responding"). We buffer here from any thread and flush on a UI timer, and cap
    // the log so its TextBox Text never grows unbounded (which was O(n²) per line).
    private readonly object _uiLock = new();
    private readonly Queue<string> _pendingLog = new();
    private readonly LinkedList<string> _logLines = new();
    private const int MaxLogLines = 500;
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
        DeleteInstanceButton.Click += OnDeleteInstance;
        AddInstanceButton.Click += (_, _) => OpenInstanceDialog(isNew: true);
        EditInstanceButton.Click += (_, _) => OpenInstanceDialog(isNew: false);
        CancelInstanceButton.Click += (_, _) => InstanceOverlay.IsVisible = false;
        OpenFolderButton.Click += OnOpenInstanceFolder;
        NewInstanceButton.Click += OnCreateInstance;
        SaveInstanceButton.Click += OnSaveInstance;
        BrowseJavaButton.Click += OnBrowseJava;

        MicrosoftLoginButton.Click += OnMicrosoftLogin;
        MicrosoftLogoutButton.Click += OnMicrosoftLogout;
        PlayButton.Click += OnPlay;
        StopButton.Click += (_, _) => { _launchCts?.Cancel(); _core.Game.Stop(); };

        ModInstanceCombo.SelectionChanged += (_, _) => RefreshMods();
        RefreshModsButton.Click += (_, _) => RefreshMods();
        AddModButton.Click += OnAddMod;
        RemoveModButton.Click += OnRemoveMod;
        ToggleModButton.Click += OnToggleMod;
        ModrinthSearchButton.Click += OnModrinthSearch;
        ModrinthList.SelectionChanged += OnModrinthResultSelected;
        ModrinthDownloadButton.Click += OnModrinthDownload;
        // Enter in the search box triggers the search.
        ModrinthQueryBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; OnModrinthSearch(s, e); }
        };
        // Left navigation: swap the visible section; refresh mods when opening Mods.
        NavView.SelectionChanged += OnNavSelectionChanged;
        NavVersion.Text = "v" + AppInfo.Version;
        NavView.SelectedItem = NavHome;

        // --- modpack (.frpack) + skin profiles ---
        ExportPackButton.Click += OnExportPack;
        ImportPackButton.Click += OnImportPack;
        ImportMrpackButton.Click += OnImportMrpack;
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
        _core.Game.Log += (_, line) => AppendLog(line);
        // RunningChanged is rare (twice a session) and flips button state — keep it immediate.
        _core.Game.RunningChanged += (_, running) => Dispatcher.UIThread.Post(() =>
        {
            StopButton.IsEnabled = running;
            PlayButton.IsEnabled = !running;
            PlayStatus.Text = running ? Loc.T("status.running") : Loc.T("status.ended");
        });

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
        NavMods.Content = Loc.T("nav.mods");
        NavAccounts.Content = Loc.T("nav.accounts");
        NavSettings.Content = Loc.T("nav.settings");

        // Instances tab
        LblInstances.Text = Loc.T("instances.list");
        AddInstanceLabel.Text = Loc.T("home.newtitle");
        EditInstanceButton.Content = Loc.T("btn.edit");
        OpenFolderButton.Content = Loc.T("btn.folder");
        CancelInstanceButton.Content = Loc.T("btn.cancel");
        RefreshInstancesButton.Content = Loc.T("btn.refresh");
        DeleteInstanceButton.Content = Loc.T("btn.delete");
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
        ExportPackButton.Content = Loc.T("btn.exportpack");
        ImportPackButton.Content = Loc.T("btn.importpack");
        ImportMrpackButton.Content = Loc.T("btn.importmrpack");

        // Play tab
        LblProfileOffline.Text = Loc.T("label.profile");
        RefreshPlayAccountList();
        MicrosoftLoginButton.Content = Loc.T("btn.mslogin");
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
        LblInstalledMods.Text = Loc.T("label.installedmods");
        AddModButton.Content = Loc.T("btn.addjar");
        RemoveModButton.Content = Loc.T("btn.remove");
        ToggleModButton.Content = Loc.T("btn.toggle");
        ModrinthQueryBox.Watermark = Loc.T("watermark.modrinth");
        ModrinthSearchButton.Content = Loc.T("btn.search");
        LblModVersion.Text = Loc.T("label.version");
        ModrinthDownloadButton.Content = Loc.T("btn.downloadinstance");

        // Skin tab — same profile/nick wording as the Play tab.
        LblSkinProfile.Text = Loc.T("label.profile");
        NewProfileButton.Content = Loc.T("btn.new");
        DeleteProfileButton.Content = Loc.T("btn.deleteprofile");
        LblProfileName.Text = Loc.T("label.nick");
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
    /// files over the install folder, relaunches, and deletes itself — then shuts down.
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
        if (p == null) { SkinStatus.Text = Loc.T("skin.selectorcreate"); return; }

        var newName = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? p.Name : ProfileNameBox.Text!.Trim();
        var nameChanged = !string.Equals(newName, p.Name, StringComparison.Ordinal);

        // Warn (once) that changing an offline nick changes the identity/UUID and can lose
        // server progress. Skin/cape stay on the profile — this is only about the nick.
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

        var inst = idx >= 0 && idx < _instances.Count ? _instances[idx] : null;
        _selected.Current = inst;
        ModsInstanceName.Text = inst?.Name ?? "—";
        UpdateModsAvailability();
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
        DownloadProgress.Value = 0;
        PlayStatus.Text = Loc.T("play.preparing");
        try
        {
            // Auto-apply the active profile's skin/cape (offline) so the user never has to
            // "apply by name" — it just follows the profile, even after a rename.
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
        }
        catch (Exception ex)
        {
            CrashLog.Write("[play] launch failed", ex);
            AppendLog(Loc.T("log.error") + ex.Message);
            PlayStatus.Text = Loc.T("status.error", ex.Message);
            LogDrawer.IsExpanded = true;
            PlayButton.IsEnabled = true;
        }
    });

    /// <summary>Queues a log line from any thread; the UI timer renders it (capped/batched).</summary>
    private void AppendLog(string line)
    {
        lock (_uiLock) _pendingLog.Enqueue(line);
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

        var inst = SelectedModInstance();
        ModsInstanceName.Text = inst?.Name ?? "—";
        if (inst == null) { _mods = new(); ModsList.ItemsSource = null; return; }

        _mods = _core.Mods.ListMods(inst).ToList();
        ModsList.ItemsSource = _mods
            .Select(m => (m.Enabled ? "[on]  " : "[off] ") + m.DisplayName)
            .ToList();
    }

    /// <summary>
    /// Mods can't go into a Vanilla instance. Rather than let the install fail into the
    /// log (where users missed it), we disable the install controls, show a clear inline
    /// notice, and offer a shortcut to create a modded instance.
    /// </summary>
    private void UpdateModsAvailability()
    {
        var inst = SelectedModInstance();
        var isVanilla = inst != null && inst.Loader == LoaderType.Vanilla;

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

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("picker.mod"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(Loc.T("filetype.jar")) { Patterns = new[] { "*.jar" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await _core.Mods.AddModAsync(inst, path);
        RefreshMods();
        Notify(Loc.T("mods.added", System.IO.Path.GetFileName(path)));
    });

    private async void OnRemoveMod(object? sender, RoutedEventArgs e) => await SafeAsync(() =>
    {
        var inst = SelectedModInstance();
        var idx = ModsList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _mods.Count) { Notify(Loc.T("mods.selectmod")); return Task.CompletedTask; }

        _core.Mods.RemoveMod(inst, _mods[idx].FileName);
        RefreshMods();
        Notify(Loc.T("mods.removed"));
        return Task.CompletedTask;
    });

    private async void OnToggleMod(object? sender, RoutedEventArgs e) => await SafeAsync(() =>
    {
        var inst = SelectedModInstance();
        var idx = ModsList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _mods.Count) { Notify(Loc.T("mods.selectmod")); return Task.CompletedTask; }

        _core.Mods.ToggleMod(inst, _mods[idx].FileName);
        RefreshMods();
        return Task.CompletedTask;
    });

    private async void OnModrinthSearch(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        if (inst == null) { Notify(Loc.T("common.selectinstance")); return; }

        Notify(Loc.T("mods.searching"));
        _hits = await _core.Mods.SearchModrinthAsync(inst, ModrinthQueryBox.Text ?? "");
        _modVersions = new List<ModrinthVersion>();
        ModrinthVersionCombo.ItemsSource = null;
        ModrinthList.ItemsSource = _hits
            .Select(h => $"{h.Title}  —  {Truncate(h.Description, 60)}")
            .ToList();
        Notify(Loc.T("mods.results", _hits.Count));
    });

    /// <summary>When a search result is picked, load its versions into the chooser.</summary>
    private async void OnModrinthResultSelected(object? sender, SelectionChangedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        var idx = ModrinthList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _hits.Count)
        {
            _modVersions = new List<ModrinthVersion>();
            ModrinthVersionCombo.ItemsSource = null;
            return;
        }

        _modVersions = await _core.Mods.GetModrinthVersionsAsync(inst, _hits[idx].ProjectId);
        ModrinthVersionCombo.ItemsSource = _modVersions.Select(v => v.Display).ToList();
        if (_modVersions.Count > 0) ModrinthVersionCombo.SelectedIndex = 0;
    });

    private async void OnModrinthDownload(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        var vIdx = ModrinthVersionCombo.SelectedIndex;
        if (inst == null || vIdx < 0 || vIdx >= _modVersions.Count) { Notify(Loc.T("mods.selectresult")); return; }

        var version = _modVersions[vIdx];
        Notify(Loc.T("mods.downloading", version.Display));
        var log = new Progress<string>(f => AppendLog("[mod] " + f));
        var installed = await _core.Mods.InstallVersionAsync(inst, version, log);
        RefreshMods();
        Notify(Loc.T("mods.installedcount", installed.Count));
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
        catch { FaceImage.Source = null; } // not a real skin texture — skip the crop
    }

    private void SetCapeImage(string? path)
    {
        CapeImage.Source = string.IsNullOrEmpty(path) || !File.Exists(path) ? null : new Bitmap(path);
    }

    // ============================== HELPERS ==============================

    /// <summary>Minimal modal yes/no dialog (bare UI: no MessageBox in Avalonia).</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var yes = new Button { Content = Loc.T("btn.continue") };
        var no = new Button { Content = Loc.T("btn.cancel") };

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
