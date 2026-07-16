// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.ComponentModel;
using Avalonia.Media.Imaging;
using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// An installed mod adapted for the list card: starts with the file name as a title
/// and is enriched in place (real name, version, icon) once metadata resolves, so
/// the card updates without rebuilding the list.
/// </summary>
public sealed class InstalledModVm : INotifyPropertyChanged
{
    public InstalledModVm(ModItem item)
    {
        Item = item;
        _title = item.DisplayName;
    }

    public ModItem Item { get; }
    public bool Enabled => Item.Enabled;

    private string _title;
    public string Title
    {
        get => _title;
        private set { if (_title != value) { _title = value; Raise(nameof(Title)); } }
    }

    private string? _version;
    public string? Version
    {
        get => _version;
        private set
        {
            if (_version == value) return;
            _version = value;
            Raise(nameof(Version));
            Raise(nameof(HasVersion));
        }
    }

    public bool HasVersion => !string.IsNullOrEmpty(_version);

    private string? _description;
    public string? Description
    {
        get => _description;
        private set
        {
            if (_description == value) return;
            _description = value;
            Raise(nameof(Description));
            Raise(nameof(HasDescription));
        }
    }

    public bool HasDescription => !string.IsNullOrEmpty(_description);

    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon;
        private set
        {
            if (_icon == value) return;
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon != null;

    /// <summary>Applies resolved metadata; a null icon leaves the placeholder in place.</summary>
    public void Apply(string title, string? version, string? description, Bitmap? icon)
    {
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        Version = version;
        Description = description;
        if (icon != null) Icon = icon;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
