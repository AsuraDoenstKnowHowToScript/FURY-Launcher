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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CmlLib.Core.Auth;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core;
using Launcher.Core.Localization;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// The Accounts screen. Owns the unified account list (offline + Microsoft), the active
/// account — the single source of truth for who launches — and the editor state for the
/// selected card. Talks to <see cref="LauncherCore"/> for data and to
/// <see cref="IDialogService"/> for anything that needs the window (dialogs, picker, toast),
/// so the screen carries no reference to the window itself.
/// </summary>
public sealed class AccountsViewModel : ViewModelBase
{
    private readonly LauncherCore _core;
    private readonly SelectedInstanceService _selected;
    private readonly IDialogService _dialogs;

    private List<Account> _accounts = new();
    private bool _suppressSelection;

    public AccountsViewModel(LauncherCore core, SelectedInstanceService selected, IDialogService dialogs)
    {
        _core = core;
        _selected = selected;
        _dialogs = dialogs;

        AddOfflineCommand = new AsyncRelayCommand(AddOfflineAsync);
        AddMicrosoftCommand = new AsyncRelayCommand(AddMicrosoftAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        ChooseSkinCommand = new AsyncRelayCommand(ChooseSkinAsync);
        ChooseCapeCommand = new AsyncRelayCommand(ChooseCapeAsync);
        ApplySkinCommand = new AsyncRelayCommand(ApplySkinAsync);

        _instanceName = _selected.Current?.Name ?? "—";
        _selected.Changed += (_, inst) => InstanceName = inst?.Name ?? "—";

        // Localized text is exposed as computed properties; one blanket notification
        // re-reads every one of them when the language changes.
        Loc.Changed += RefreshTexts;
    }

    // ------------------------------- data -------------------------------

    /// <summary>Card per account; the list is the selector.</summary>
    public ObservableCollection<AccountCardVm> Cards { get; } = new();

    /// <summary>The account that launches. Mirror of <c>settings.ActiveAccountId</c>.</summary>
    public Account? ActiveAccount { get; private set; }

    /// <summary>Resolved session for the active Microsoft account, reused by the launch path.</summary>
    public MSession? ActiveMsSession { get; set; }

    /// <summary>Raised when the active account (or its avatar) changes, so Home can refresh its chip.</summary>
    public event EventHandler? ActiveAccountChanged;

    private Account? SelectedAccount => _accounts.FirstOrDefault(a => a.Id == _selectedCard?.Id);

    private AccountCardVm? _selectedCard;
    public AccountCardVm? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (!SetProperty(ref _selectedCard, value) || _suppressSelection) return;
            var acc = SelectedAccount;
            if (acc == null) return;

            // Selecting a card is what activates the account.
            _ = _dialogs.RunGuardedAsync(async () =>
            {
                await _core.Accounts.SetActiveAsync(acc.Id);
                ActiveAccount = acc;
                ActiveMsSession = null; // re-resolved on demand for the newly active account
                foreach (var v in Cards) v.IsActive = v.Id == acc.Id;
                RaiseActiveChanged();
                LoadEditor(acc);
                if (acc.Kind == AccountKind.Microsoft) await ResolveMicrosoftAsync();
            });
        }
    }

    // ------------------------------ commands ------------------------------

    public IAsyncRelayCommand AddOfflineCommand { get; }
    public IAsyncRelayCommand AddMicrosoftCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ChooseSkinCommand { get; }
    public IAsyncRelayCommand ChooseCapeCommand { get; }
    public IAsyncRelayCommand ApplySkinCommand { get; }

    // ------------------------------- state -------------------------------

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

    private bool _editorVisible;
    public bool EditorVisible
    {
        get => _editorVisible;
        private set { if (SetProperty(ref _editorVisible, value)) OnPropertyChanged(nameof(HintVisible)); }
    }

    /// <summary>"Select an account" placeholder: shown exactly when the editor is not.</summary>
    public bool HintVisible => !_editorVisible;

    private bool _canEdit = true;
    /// <summary>False on a Microsoft account: its nick and skin are managed by Mojang.</summary>
    public bool CanEdit { get => _canEdit; private set => SetProperty(ref _canEdit, value); }

    private bool _msNoteVisible;
    public bool MsNoteVisible { get => _msNoteVisible; private set => SetProperty(ref _msNoteVisible, value); }

    private string? _skinTooltip;
    public string? SkinTooltip { get => _skinTooltip; private set => SetProperty(ref _skinTooltip, value); }

    private string _nick = "";
    public string Nick { get => _nick; set => SetProperty(ref _nick, value); }

    private bool _isSlim;
    public bool IsSlim { get => _isSlim; set => SetProperty(ref _isSlim, value); }

    private IImage? _facePreview;
    public IImage? FacePreview { get => _facePreview; private set => SetProperty(ref _facePreview, value); }

    private IImage? _capePreview;
    public IImage? CapePreview { get => _capePreview; private set => SetProperty(ref _capePreview, value); }

    private IImage? _skinPreview;
    public IImage? SkinPreview { get => _skinPreview; private set => SetProperty(ref _skinPreview, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _accountStatusText = "";
    public string AccountStatusText { get => _accountStatusText; private set => SetProperty(ref _accountStatusText, value); }

    private string _instanceName;
    public string InstanceName { get => _instanceName; private set => SetProperty(ref _instanceName, value); }

    private bool _isAddingMicrosoft;
    public bool IsAddingMicrosoft { get => _isAddingMicrosoft; private set => SetProperty(ref _isAddingMicrosoft, value); }

    private bool _isApplyingSkin;
    public bool IsApplyingSkin { get => _isApplyingSkin; private set => SetProperty(ref _isApplyingSkin, value); }

    // --------------------------- localized text ---------------------------

    public string HeaderText => Loc.T("skins.account");
    public string ListHeaderText => Loc.T("account.section");
    public string AppearanceHeaderText => Loc.T("skins.appearance");
    public string AddOfflineText => Loc.T("btn.addoffline");
    public string AddMicrosoftText => Loc.T("btn.addms");
    public string DeleteText => Loc.T("btn.delete");
    public string EmptyText => Loc.T("account.none");
    public string SelectHintText => Loc.T("account.selecthint");
    public string MsManagedText => Loc.T("account.msmanaged");
    public string NickLabel => Loc.T("label.nick");
    public string NickHintText => Loc.T("skins.nickhint");
    public string SlimText => Loc.T("check.slim");
    public string SaveText => Loc.T("btn.saveprofile");
    public string ChooseSkinText => Loc.T("btn.chooseskin");
    public string ChooseCapeText => Loc.T("btn.choosecape");
    public string ApplyInstanceLabel => Loc.T("label.applyinstance");
    public string ApplyText => Loc.T("btn.applyingame");
    public string SkinPreviewLabel => Loc.T("label.skinpreview");
    public string FacePreviewLabel => Loc.T("label.facepreview");
    public string CapePreviewLabel => Loc.T("label.capepreview");
    public string Help1Text => Loc.T("skin.help1");
    public string Help2Text => Loc.T("skin.help2");

    /// <summary>Empty name = "everything changed", so every computed string is re-read.</summary>
    private void RefreshTexts() => OnPropertyChanged(string.Empty);

    // ------------------------------ behaviour ------------------------------

    /// <summary>
    /// Reloads the account list, rebuilds the cards, refreshes the editor, then resolves
    /// Microsoft sessions/avatars in the background (silent resume).
    /// </summary>
    public async Task RefreshAsync()
    {
        _accounts = (await _core.Accounts.ListAsync()).ToList();
        ActiveAccount = _accounts.FirstOrDefault(a => a.IsActive) ?? _accounts.FirstOrDefault();
        if (ActiveAccount != null && !_accounts.Any(a => a.IsActive))
        {
            await _core.Accounts.SetActiveAsync(ActiveAccount.Id);
            ActiveAccount.IsActive = true;
        }

        RebuildCards();
        RaiseActiveChanged();
        LoadEditor(ActiveAccount);
        _ = ResolveMicrosoftAsync();
    }

    private void RebuildCards()
    {
        var keepId = _selectedCard?.Id ?? ActiveAccount?.Id;
        Cards.Clear();
        foreach (var a in _accounts)
        {
            var vm = new AccountCardVm(a, BadgeFor(a));
            if (a.Kind == AccountKind.Offline && a.SkinPath != null && File.Exists(a.SkinPath))
            {
                try { vm.SetSkin(new Bitmap(a.SkinPath)); } catch { /* not a real skin: keep glyph */ }
            }
            Cards.Add(vm);
        }
        IsEmpty = Cards.Count == 0;

        _suppressSelection = true;
        SelectedCard = Cards.FirstOrDefault(v => v.Id == keepId)
                       ?? Cards.FirstOrDefault(v => v.Id == ActiveAccount?.Id);
        _suppressSelection = false;
    }

    private static string BadgeFor(Account a)
        => Loc.T(a.Kind == AccountKind.Microsoft ? "account.badge.ms" : "account.badge.offline");

    private void SelectById(string id)
    {
        _suppressSelection = true;
        SelectedCard = Cards.FirstOrDefault(v => v.Id == id);
        _suppressSelection = false;
        LoadEditor(_accounts.FirstOrDefault(a => a.Id == id));
    }

    /// <summary>Fills the editor for the selected account, locking nick/skin on Microsoft.</summary>
    private void LoadEditor(Account? acc)
    {
        if (acc == null)
        {
            EditorVisible = false;
            return;
        }
        EditorVisible = true;

        var isMs = acc.Kind == AccountKind.Microsoft;
        Nick = acc.Username;
        IsSlim = acc.Slim;
        CanEdit = !isMs;
        MsNoteVisible = isMs;
        SkinTooltip = isMs ? Loc.T("account.msmanaged.tip") : null;

        if (isMs)
        {
            SetSkinImages(null);
            SetCapeImage(null);
            StatusText = Loc.T("account.msmanaged");
            _ = LoadMicrosoftPreviewAsync(acc);
        }
        else
        {
            SetSkinImages(acc.SkinPath);
            SetCapeImage(acc.CapePath);
            StatusText = Loc.T("skin.profileinfo", acc.Username,
                Loc.T(acc.SkinPath != null ? "skin.hasskin" : "skin.noskin"),
                Loc.T(acc.CapePath != null ? "skin.hascape" : "skin.nocape"),
                Loc.T(acc.Slim ? "model.slim" : "model.classic"));
        }
    }

    /// <summary>Loads the read-only Mojang skin into the preview for a Microsoft account.</summary>
    private async Task LoadMicrosoftPreviewAsync(Account acc)
    {
        if (string.IsNullOrEmpty(acc.Uuid)) return;
        var skin = await _core.MsSkins.GetAsync(acc.Uuid);
        if (SelectedAccount?.Id != acc.Id) return; // selection moved on while we fetched
        if (skin != null && File.Exists(skin.PngPath))
        {
            SetSkinImages(skin.PngPath);
            var card = Cards.FirstOrDefault(v => v.Id == acc.Id);
            try { card?.SetSkin(new Bitmap(skin.PngPath)); } catch { }
            RaiseActiveChanged();
        }
    }

    /// <summary>
    /// Silently resumes each cached Microsoft account to fill its nick/uuid + avatar, flagging
    /// the ones whose token expired. Runs in the background; never throws to the UI.
    /// </summary>
    private async Task ResolveMicrosoftAsync()
    {
        foreach (var acc in _accounts.Where(a => a.Kind == AccountKind.Microsoft && !string.IsNullOrEmpty(a.MsAccountRef)).ToList())
        {
            MSession? s = null;
            try { s = await _core.Auth.TryResumeMicrosoftAsync(acc.MsAccountRef!); } catch { }
            var card = Cards.FirstOrDefault(v => v.Id == acc.Id);
            if (s != null)
            {
                await _core.Accounts.UpsertMicrosoftAsync(acc.MsAccountRef!, s.Username ?? "", s.UUID ?? "");
                acc.Username = string.IsNullOrWhiteSpace(s.Username) ? acc.Username : s.Username!;
                acc.Uuid = string.IsNullOrWhiteSpace(s.UUID) ? acc.Uuid : s.UUID!;
                if (ActiveAccount?.Id == acc.Id) ActiveMsSession = s;
                if (card != null) { card.Username = acc.Username; card.Badge = Loc.T("account.badge.ms"); }
                if (!string.IsNullOrEmpty(acc.Uuid))
                {
                    var skin = await _core.MsSkins.GetAsync(acc.Uuid);
                    if (skin != null && File.Exists(skin.PngPath))
                        try { card?.SetSkin(new Bitmap(skin.PngPath)); } catch { }
                }
            }
            else if (card != null)
            {
                card.Badge = Loc.T("account.badge.expired");
            }
        }
        RaiseActiveChanged();
        if (ActiveAccount?.Kind == AccountKind.Microsoft && SelectedAccount?.Id == ActiveAccount.Id)
            Nick = ActiveAccount.Username;
    }

    private async Task AddOfflineAsync()
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            // A unique starter nick so two "New account"s never clash.
            var baseName = Loc.T("profile.newname");
            var name = baseName;
            var n = 2;
            while (_accounts.Any(a => a.Kind == AccountKind.Offline && string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} {n++}";

            var acc = await _core.Accounts.CreateOfflineAsync(name, false);
            await _core.Accounts.SetActiveAsync(acc.Id);
            await RefreshAsync();
            SelectById(acc.Id);
            StatusText = Loc.T("skin.profilecreated", acc.Username);
        });
    }

    private async Task AddMicrosoftAsync()
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            IsAddingMicrosoft = true;
            AccountStatusText = Loc.T("ms.opening");
            try
            {
                var (session, reff) = await _core.Auth.LoginMicrosoftWithRefAsync();
                var key = string.IsNullOrEmpty(reff) ? (session.UUID ?? Guid.NewGuid().ToString("N")) : reff!;
                var acc = await _core.Accounts.UpsertMicrosoftAsync(key, session.Username ?? "", session.UUID ?? "");
                await _core.Accounts.SetActiveAsync(acc.Id);
                ActiveMsSession = session;
                await RefreshAsync();
                SelectById(acc.Id);
                AccountStatusText = Loc.T("account.ms", session.Username);
            }
            finally
            {
                IsAddingMicrosoft = false;
            }
        });
    }

    private async Task SaveAsync()
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            var acc = SelectedAccount;
            if (acc == null)
            {
                StatusText = Loc.T("skin.selectorcreate");
                _dialogs.Toast(Loc.T("skin.selectorcreate"), error: true);
                return;
            }
            if (acc.Kind != AccountKind.Offline) return; // Microsoft nick/skin are read-only

            var newName = (Nick ?? "").Trim();
            if (newName.Length == 0)
            {
                StatusText = Loc.T("skin.nickrequired");
                _dialogs.Toast(Loc.T("skin.nickrequired"), error: true);
                return;
            }
            var slim = IsSlim;
            var nameChanged = !string.Equals(newName, acc.Username, StringComparison.Ordinal);

            if (nameChanged)
            {
                // Renaming an offline account changes its UUID (the identity); warn once.
                var settings = await _core.Settings.LoadAsync();
                if (!settings.SuppressNickChangeWarning)
                {
                    var (proceed, dontShow) = await _dialogs.WarnAckAsync(
                        Loc.T("warn.nicktitle"),
                        Loc.T("warn.nickmsg", acc.Username, newName),
                        Loc.T("warn.nickack"));
                    if (!proceed) { StatusText = Loc.T("skin.nickcancelled"); return; }
                    if (dontShow) { settings.SuppressNickChangeWarning = true; await _core.Settings.SaveAsync(settings); }
                }

                var old = await _core.Accounts.RenameOfflineAsync(acc.Id, newName);
                var instances = await _core.Instances.ListAsync();
                await _core.Skins.RenameLocalSkinAsync(instances, old, newName);
                acc = (await _core.Accounts.ListAsync()).FirstOrDefault(a => a.Id == acc.Id) ?? acc;
            }

            if (acc.Slim != slim)
            {
                acc.Slim = slim;
                await _core.Accounts.UpdateAsync(acc);
            }

            await RefreshAsync();
            SelectById(acc.Id);
            StatusText = Loc.T("skin.profilesaved", newName, Loc.T(slim ? "model.slim" : "model.classic"));
        });
    }

    private async Task DeleteAsync()
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            var acc = SelectedAccount;
            if (acc == null) return;
            if (!await _dialogs.ConfirmAsync(Loc.T("account.remove.title"), Loc.T("account.remove.msg", acc.Username))) return;

            if (acc.Kind == AccountKind.Microsoft && !string.IsNullOrEmpty(acc.MsAccountRef))
                await _core.Auth.SignOutMicrosoftAsync(acc.MsAccountRef!);
            if (ActiveAccount?.Id == acc.Id) ActiveMsSession = null;

            await _core.Accounts.DeleteAsync(acc.Id);
            await RefreshAsync();
            StatusText = Loc.T("skin.profiledeleted", acc.Username);
        });
    }

    private Task ChooseSkinAsync() => ChooseImageAsync(isSkin: true);
    private Task ChooseCapeAsync() => ChooseImageAsync(isSkin: false);

    private async Task ChooseImageAsync(bool isSkin)
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            var acc = SelectedAccount;
            if (acc == null || acc.Kind != AccountKind.Offline) { StatusText = Loc.T("skin.createselectfirst"); return; }
            var path = await _dialogs.PickImageAsync(Loc.T(isSkin ? "picker.skin" : "picker.cape"));
            if (path == null) return;

            if (isSkin) await _core.Accounts.SetSkinAsync(acc, path);
            else await _core.Accounts.SetCapeAsync(acc, path);

            await RefreshAsync();
            SelectById(acc.Id);
        });
    }

    private async Task ApplySkinAsync()
    {
        await _dialogs.RunGuardedAsync(async () =>
        {
            var acc = SelectedAccount;
            if (acc == null || acc.Kind != AccountKind.Offline) { StatusText = Loc.T("skin.createselect"); return; }
            // The instance comes from the shared Home selection, same as before.
            var inst = _selected.Current;
            if (inst == null) { StatusText = Loc.T("skin.selectinstanceapply"); return; }

            IsApplyingSkin = true;
            StatusText = Loc.T("skin.applying");
            try
            {
                var log = new Progress<string>(_dialogs.Log);
                await _core.Skins.ApplyOfflineAsync(inst, acc, log);
                StatusText = Loc.T("skin.applied", acc.Username, inst.Name);
            }
            finally
            {
                IsApplyingSkin = false;
            }
        });
    }

    // ------------------------------ previews ------------------------------

    private void SetSkinImages(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SkinPreview = null;
            FacePreview = null;
            return;
        }

        var bmp = new Bitmap(path);
        SkinPreview = bmp;

        // Cheap face preview: the 8x8 face region lives at (8,8) in every skin texture
        // (64x64 and legacy 64x32). Nearest-neighbor upscaling keeps it crisp.
        try { FacePreview = new CroppedBitmap(bmp, new PixelRect(8, 8, 8, 8)); }
        catch { FacePreview = null; } // not a real skin texture, skip the crop
    }

    private void SetCapeImage(string? path)
        => CapePreview = string.IsNullOrEmpty(path) || !File.Exists(path) ? null : new Bitmap(path);

    private void RaiseActiveChanged() => ActiveAccountChanged?.Invoke(this, EventArgs.Empty);
}
