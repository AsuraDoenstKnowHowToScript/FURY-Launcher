// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// One account rendered as a selectable card: a composited head avatar (base face at
/// (8,8) plus the hat overlay at (40,8) of the skin PNG), the nick, a kind/status badge,
/// and whether it is the active account. The avatar is filled in place — synchronously for
/// offline skins, asynchronously for Microsoft skins fetched from Mojang — so the card
/// updates without rebuilding the list.
/// </summary>
public sealed class AccountCardVm : INotifyPropertyChanged
{
    public AccountCardVm(Account account, string badge)
    {
        Id = account.Id;
        _username = string.IsNullOrWhiteSpace(account.Username) ? "—" : account.Username;
        IsMicrosoft = account.Kind == AccountKind.Microsoft;
        Slim = account.Slim;
        _isActive = account.IsActive;
        _badge = badge;
    }

    public string Id { get; }
    public bool IsMicrosoft { get; }
    public bool Slim { get; }

    private string _username;
    /// <summary>Nick shown on the card; updated in place once a Microsoft session resolves.</summary>
    public string Username
    {
        get => _username;
        set { var v = string.IsNullOrWhiteSpace(value) ? "—" : value; if (_username != v) { _username = v; Raise(nameof(Username)); } }
    }

    private string _badge;
    public string Badge
    {
        get => _badge;
        set { if (_badge != value) { _badge = value; Raise(nameof(Badge)); } }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); } }
    }

    private IImage? _face;
    /// <summary>The 8×8 base face region of the skin, upscaled by the view (nearest-neighbor).</summary>
    public IImage? Face
    {
        get => _face;
        private set { _face = value; Raise(nameof(Face)); Raise(nameof(HasFace)); }
    }

    private IImage? _hat;
    /// <summary>The 8×8 hat/overlay region drawn on top of the face.</summary>
    public IImage? Hat
    {
        get => _hat;
        private set { _hat = value; Raise(nameof(Hat)); }
    }

    /// <summary>True once a real face crop is available; the view shows a fallback glyph otherwise.</summary>
    public bool HasFace => _face != null;

    /// <summary>
    /// Builds the face + hat crops from a full skin bitmap. A null or non-skin bitmap clears
    /// the avatar so the card falls back to its default glyph. Never throws.
    /// </summary>
    public void SetSkin(Bitmap? skin)
    {
        if (skin == null) { Face = null; Hat = null; return; }
        try
        {
            Face = new CroppedBitmap(skin, new PixelRect(8, 8, 8, 8));
            Hat = new CroppedBitmap(skin, new PixelRect(40, 8, 8, 8));
        }
        catch
        {
            Face = null;
            Hat = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
