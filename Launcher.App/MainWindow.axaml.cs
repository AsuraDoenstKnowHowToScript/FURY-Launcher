// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CmlLib.Core.Auth;
using Launcher.Core;
using Launcher.Core.Models;

namespace Launcher.App;

/// <summary>
/// The entire (bare) UI. All real work is delegated to <see cref="LauncherCore"/>;
/// this file only reads/writes controls and marshals Core events to the UI thread.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LauncherCore _core = new();
    private readonly LoaderType[] _loaders = Enum.GetValues<LoaderType>();

    private List<Instance> _instances = new();
    private List<ModItem> _mods = new();
    private List<OfflineProfile> _profiles = new();
    private IReadOnlyList<ModrinthHit> _hits = new List<ModrinthHit>();

    private Instance? _editing;      // instance selected for editing (Instâncias tab)
    private MSession? _msSession;    // cached Microsoft session after login
    private CancellationTokenSource? _launchCts;

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
        AboutVersion.Text = $"versão {AppInfo.Version}";
        AboutCopyright.Text = AppInfo.Copyright;

        LoaderCombo.ItemsSource = _loaders.Select(l => l.ToString()).ToList();
        LoaderCombo.SelectedIndex = 0;
        AccountCombo.ItemsSource = new[] { "Offline", "Microsoft" };
        AccountCombo.SelectedIndex = 0;
        StopButton.IsEnabled = false;

        // --- wire events ---
        InstancesList.SelectionChanged += OnInstanceSelected;
        RefreshInstancesButton.Click += async (_, _) => await SafeAsync(RefreshInstancesAsync);
        DeleteInstanceButton.Click += OnDeleteInstance;
        NewInstanceButton.Click += OnCreateInstance;
        SaveInstanceButton.Click += OnSaveInstance;
        BrowseJavaButton.Click += OnBrowseJava;

        MicrosoftLoginButton.Click += OnMicrosoftLogin;
        PlayButton.Click += OnPlay;
        StopButton.Click += (_, _) => { _launchCts?.Cancel(); _core.Game.Stop(); };

        ModInstanceCombo.SelectionChanged += (_, _) => RefreshMods();
        RefreshModsButton.Click += (_, _) => RefreshMods();
        AddModButton.Click += OnAddMod;
        RemoveModButton.Click += OnRemoveMod;
        ToggleModButton.Click += OnToggleMod;
        ModrinthSearchButton.Click += OnModrinthSearch;
        ModrinthDownloadButton.Click += OnModrinthDownload;
        // Enter in the search box triggers the search.
        ModrinthQueryBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; OnModrinthSearch(s, e); }
        };
        // Auto-load the installed mods list when the Mods tab is opened.
        MainTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, MainTabs) && ReferenceEquals(MainTabs.SelectedItem, ModsTab))
                RefreshMods();
        };

        // --- modpack (.frpack) + skin profiles ---
        ExportPackButton.Click += OnExportPack;
        ImportPackButton.Click += OnImportPack;
        SkinProfileCombo.SelectionChanged += OnSkinProfileSelected;
        NewProfileButton.Click += OnNewProfile;
        SaveProfileButton.Click += OnSaveProfile;
        DeleteProfileButton.Click += OnDeleteProfile;
        ChooseSkinButton.Click += OnChooseSkin;
        ChooseCapeButton.Click += OnChooseCape;
        ApplySkinButton.Click += OnApplySkin;

        // --- Core → UI: buffer high-frequency events; a timer flushes them ---
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
            PlayStatus.Text = running ? "Rodando..." : "Encerrado.";
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _ = SafeAsync(InitAsync);
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
        await RefreshInstancesAsync();
        await RefreshProfilesAsync();
        // Restore a cached Microsoft session silently, if any.
        _msSession = await _core.Auth.TryResumeMicrosoftAsync();
        if (_msSession != null)
            AccountStatus.Text = "MS: " + _msSession.Username;
    }

    // ============================ PERFIS (offline) ============================

    private async Task RefreshProfilesAsync()
    {
        _profiles = (await _core.Profiles.ListEnsuredAsync()).ToList();
        var names = _profiles.Select(p => p.Name + (p.Slim ? "  (slim)" : "")).ToList();

        var skinSel = SkinProfileCombo.SelectedIndex;
        var playSel = OfflineProfileCombo.SelectedIndex;
        SkinProfileCombo.ItemsSource = names.ToList();
        OfflineProfileCombo.ItemsSource = names.ToList();

        if (_profiles.Count > 0)
        {
            SkinProfileCombo.SelectedIndex = skinSel >= 0 && skinSel < _profiles.Count ? skinSel : 0;
            OfflineProfileCombo.SelectedIndex = playSel >= 0 && playSel < _profiles.Count ? playSel : 0;
        }
    }

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
        SkinStatus.Text = $"Perfil '{p.Name}': " +
            (p.SkinPath != null ? "skin" : "sem skin") + " · " +
            (p.CapePath != null ? "capa" : "sem capa") + $" · modelo {(p.Slim ? "slim" : "clássico")}";
    }

    private async void OnNewProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Novo perfil" : ProfileNameBox.Text!.Trim();
        var p = await _core.Profiles.CreateAsync(name, SlimCheck.IsChecked ?? false);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
        SkinStatus.Text = $"Perfil criado: {p.Name}";
    });

    private async void OnSaveProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = "Selecione ou crie um perfil."; return; }

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
                    "Trocar o nick da conta offline",
                    $"Você vai renomear '{p.Name}' para '{newName}'.\n\n" +
                    "Conta OFFLINE não é uma conta Microsoft: ela é identificada por um UUID gerado " +
                    "a partir do NOME. Ou seja, o nick É a identidade.\n\n" +
                    "Se você tem progresso em servidores que aceitam conta offline/cracked " +
                    "(ex.: MushMC) — rank, dinheiro, casas, stats — tudo isso está preso ao UUID " +
                    "do nick ATUAL. Ao mudar o nick, o servidor te vê como um jogador NOVO e você " +
                    "perde o acesso a esse progresso (numa conta Microsoft isso não aconteceria, " +
                    "porque o UUID é fixo e persistente).\n\n" +
                    "A skin e a capa do perfil continuam salvas aqui no launcher — não se perdem.",
                    "Estou ciente");
                if (!proceed) { SkinStatus.Text = "Troca de nick cancelada."; return; }
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
        SkinStatus.Text = $"Perfil salvo: {p.Name} (modelo {(p.Slim ? "slim" : "clássico")})";
    });

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) return;
        await _core.Profiles.DeleteAsync(p.Id);
        await RefreshProfilesAsync();
        SkinStatus.Text = $"Perfil excluído: {p.Name}";
    });

    // ============================ INSTÂNCIAS ============================

    private async Task RefreshInstancesAsync()
    {
        _instances = (await _core.Instances.ListAsync()).ToList();
        var labels = _instances.Select(i => $"{i.Name}  [{i.McVersion} / {i.Loader}]").ToList();

        InstancesList.ItemsSource = labels;
        PlayInstanceCombo.ItemsSource = labels.ToList();
        ModInstanceCombo.ItemsSource = labels.ToList();
        SkinInstanceCombo.ItemsSource = labels.ToList();

        if (_instances.Count > 0)
        {
            if (PlayInstanceCombo.SelectedIndex < 0) PlayInstanceCombo.SelectedIndex = 0;
            if (ModInstanceCombo.SelectedIndex < 0) ModInstanceCombo.SelectedIndex = 0;
            if (SkinInstanceCombo.SelectedIndex < 0) SkinInstanceCombo.SelectedIndex = 0;
        }
    }

    private void OnInstanceSelected(object? sender, SelectionChangedEventArgs e)
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) return;

        _editing = _instances[idx];
        NameBox.Text = _editing.Name;
        McVersionBox.Text = _editing.McVersion;
        LoaderCombo.SelectedIndex = Array.IndexOf(_loaders, _editing.Loader);
        MinRamBox.Text = _editing.MinRamMb.ToString();
        MaxRamBox.Text = _editing.MaxRamMb.ToString();
        JvmArgsBox.Text = _editing.JvmArgs;
        JavaPathBox.Text = _editing.JavaPath ?? "";
        InstanceStatus.Text = $"Editando: {_editing.Name}";
    }

    private async void OnCreateInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = await _core.Instances.CreateAsync(
            NameBox.Text ?? "", McVersionBox.Text ?? "", SelectedLoader());
        await RefreshInstancesAsync();
        InstanceStatus.Text = $"Criada: {inst.Name} (pasta {inst.FolderName})";
    });

    private async void OnSaveInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (_editing == null) { InstanceStatus.Text = "Selecione uma instância para editar."; return; }

        _editing.Name = (NameBox.Text ?? "").Trim();
        _editing.McVersion = (McVersionBox.Text ?? "").Trim();
        _editing.Loader = SelectedLoader();
        _editing.MinRamMb = ParseInt(MinRamBox.Text, 512);
        _editing.MaxRamMb = ParseInt(MaxRamBox.Text, 2048);
        _editing.JvmArgs = JvmArgsBox.Text ?? "";
        _editing.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text!.Trim();

        await _core.Instances.UpdateAsync(_editing);
        await RefreshInstancesAsync();
        InstanceStatus.Text = $"Salva: {_editing.Name}";
    });

    private async void OnDeleteInstance(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { InstanceStatus.Text = "Nada selecionado."; return; }

        var inst = _instances[idx];
        await _core.Instances.DeleteAsync(inst.Id);
        _editing = null;
        await RefreshInstancesAsync();
        InstanceStatus.Text = $"Deletada: {inst.Name}";
    });

    private async void OnBrowseJava(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Selecione o executável do Java",
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

    // ============================== JOGAR ==============================

    private async void OnMicrosoftLogin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        MicrosoftLoginButton.IsEnabled = false;
        AccountStatus.Text = "Abrindo login da Microsoft...";
        try
        {
            _msSession = await _core.Auth.LoginMicrosoftAsync();
            AccountStatus.Text = "MS: " + _msSession.Username;
            AccountCombo.SelectedIndex = 1;
        }
        finally
        {
            MicrosoftLoginButton.IsEnabled = true;
        }
    });

    private async void OnPlay(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = PlayInstanceCombo.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { PlayStatus.Text = "Selecione uma instância."; return; }
        var inst = _instances[idx];

        MSession session;
        OfflineProfile? offlineProfile = null;
        if (AccountCombo.SelectedIndex == 1)
        {
            if (_msSession == null) { PlayStatus.Text = "Faça login Microsoft primeiro."; return; }
            session = _msSession;
        }
        else
        {
            var pIdx = OfflineProfileCombo.SelectedIndex;
            offlineProfile = pIdx >= 0 && pIdx < _profiles.Count ? _profiles[pIdx] : null;
            session = _core.Auth.CreateOffline(offlineProfile?.Name ?? "Player");
        }

        _launchCts = new CancellationTokenSource();
        PlayButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        PlayStatus.Text = "Preparando...";
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
                    AppendLog("[skin] Skin nao aplicada automaticamente: " + ex.Message);
                }
            }

            await _core.Game.LaunchAsync(inst, session, _launchCts.Token);
        }
        catch (OperationCanceledException)
        {
            PlayStatus.Text = "Cancelado.";
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("[erro] " + ex.Message);
            PlayStatus.Text = "Erro: " + ex.Message;
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
        var inst = SelectedModInstance();
        if (inst == null) { _mods = new(); ModsList.ItemsSource = null; return; }

        _mods = _core.Mods.ListMods(inst).ToList();
        ModsList.ItemsSource = _mods
            .Select(m => (m.Enabled ? "[on]  " : "[off] ") + m.DisplayName)
            .ToList();
    }

    private async void OnAddMod(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        if (inst == null) { Notify("Selecione uma instância na aba Mods."); return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Selecione um mod (.jar)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Jar") { Patterns = new[] { "*.jar" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await _core.Mods.AddModAsync(inst, path);
        RefreshMods();
        Notify($"Mod adicionado: {System.IO.Path.GetFileName(path)}");
    });

    private async void OnRemoveMod(object? sender, RoutedEventArgs e) => await SafeAsync(() =>
    {
        var inst = SelectedModInstance();
        var idx = ModsList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _mods.Count) { Notify("Selecione um mod."); return Task.CompletedTask; }

        _core.Mods.RemoveMod(inst, _mods[idx].FileName);
        RefreshMods();
        Notify("Mod removido.");
        return Task.CompletedTask;
    });

    private async void OnToggleMod(object? sender, RoutedEventArgs e) => await SafeAsync(() =>
    {
        var inst = SelectedModInstance();
        var idx = ModsList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _mods.Count) { Notify("Selecione um mod."); return Task.CompletedTask; }

        _core.Mods.ToggleMod(inst, _mods[idx].FileName);
        RefreshMods();
        return Task.CompletedTask;
    });

    private async void OnModrinthSearch(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        if (inst == null) { Notify("Selecione uma instância."); return; }

        Notify("Buscando no Modrinth...");
        _hits = await _core.Mods.SearchModrinthAsync(inst, ModrinthQueryBox.Text ?? "");
        ModrinthList.ItemsSource = _hits
            .Select(h => $"{h.Title}  —  {Truncate(h.Description, 60)}")
            .ToList();
        Notify($"{_hits.Count} resultado(s).");
    });

    private async void OnModrinthDownload(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var inst = SelectedModInstance();
        var idx = ModrinthList.SelectedIndex;
        if (inst == null || idx < 0 || idx >= _hits.Count) { Notify("Selecione um resultado."); return; }

        var hit = _hits[idx];
        Notify($"Baixando {hit.Title}...");
        var path = await _core.Mods.InstallFromModrinthAsync(inst, hit.ProjectId);
        RefreshMods();
        Notify($"Baixado: {System.IO.Path.GetFileName(path)}");
    });

    // ============================ MODPACK (.frpack) ============================

    private async void OnExportPack(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var idx = InstancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { InstanceStatus.Text = "Selecione uma instância para exportar."; return; }
        var inst = _instances[idx];

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar .frpack",
            SuggestedFileName = inst.Name + ".frpack",
            DefaultExtension = "frpack",
            FileTypeChoices = new[] { new FilePickerFileType("FURY Package") { Patterns = new[] { "*.frpack" } } }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        InstanceStatus.Text = "Exportando .frpack...";
        await _core.Packs.ExportAsync(inst, path);
        InstanceStatus.Text = $"Exportado: {path}";
    });

    private async void OnImportPack(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar .frpack",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("FURY Package") { Patterns = new[] { "*.frpack" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        // Validate first and warn before importing if anything looks off.
        var preview = await _core.Packs.ReadManifestAsync(path);
        if (preview.Warnings.Count > 0)
        {
            var ok = await ConfirmAsync("Avisos ao importar o pacote",
                string.Join(Environment.NewLine, preview.Warnings) +
                Environment.NewLine + Environment.NewLine + "Importar mesmo assim?");
            if (!ok) { InstanceStatus.Text = "Importação cancelada."; return; }
        }

        InstanceStatus.Text = "Importando .frpack...";
        var inst = await _core.Packs.ImportAsync(path);
        await RefreshInstancesAsync();
        InstanceStatus.Text = $"Importado: {inst.Name} [{inst.McVersion} / {inst.Loader}]";
    });

    // =============================== SKIN / CAPA ===============================

    private async void OnChooseSkin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = "Crie/selecione um perfil primeiro."; return; }
        var path = await PickImageAsync("Selecione a skin (PNG)");
        if (path == null) return;
        await _core.Profiles.SetSkinAsync(p, path);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
    });

    private async void OnChooseCape(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = "Crie/selecione um perfil primeiro."; return; }
        var path = await PickImageAsync("Selecione a capa (PNG)");
        if (path == null) return;
        await _core.Profiles.SetCapeAsync(p, path);
        await RefreshProfilesAsync();
        SelectProfileById(p.Id);
    });

    private async void OnApplySkin(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var p = SelectedSkinProfile();
        if (p == null) { SkinStatus.Text = "Crie/selecione um perfil."; return; }
        var idx = SkinInstanceCombo.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) { SkinStatus.Text = "Selecione uma instância para aplicar."; return; }
        var inst = _instances[idx];

        ApplySkinButton.IsEnabled = false;
        SkinStatus.Text = "Aplicando (instala o CustomSkinLoader se necessário)...";
        try
        {
            var log = new Progress<string>(AppendLog);
            await _core.Skins.ApplyOfflineAsync(inst, p, log);
            SkinStatus.Text = $"Skin do perfil '{p.Name}' aplicada em '{inst.Name}'. " +
                              "Jogue com esse perfil (aba Jogar) e (re)inicie a instância.";
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
            FileTypeFilter = new[] { new FilePickerFileType("Imagem PNG") { Patterns = new[] { "*.png" } } }
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
        var yes = new Button { Content = "Continuar" };
        var no = new Button { Content = "Cancelar" };

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
        var dontShow = new CheckBox { Content = "Não mostrar novamente" };
        var ack = new Button { Content = ackButton };
        var cancel = new Button { Content = "Cancelar" };

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
        AppendLog("[info] " + message);
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
            Dispatcher.UIThread.Post(() =>
            {
                AppendLog("[erro] " + ex.Message);
                PlayStatus.Text = "Erro: " + ex.Message;
            });
        }
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, out var v) && v > 0 ? v : fallback;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
