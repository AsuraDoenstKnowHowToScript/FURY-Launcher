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
/// A Modrinth search hit adapted for the results card list: exposes display-ready
/// fields plus a lazily-loaded <see cref="Icon"/> and an <see cref="Installing"/>
/// flag, both raising change notifications so the card updates in place.
/// </summary>
public sealed class ModrinthHitVm : INotifyPropertyChanged
{
    public ModrinthHitVm(ModrinthHit hit)
    {
        Hit = hit;
        DownloadsText = "⬇ " + FormatCount(hit.Downloads);
    }

    public ModrinthHit Hit { get; }

    public string ProjectId => Hit.ProjectId;
    public string Title => Hit.Title;
    public string Author => Hit.Author ?? "";
    public string Description => Hit.Description;
    public string? IconUrl => Hit.IconUrl;
    public string DownloadsText { get; }

    private Bitmap? _icon;
    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(HasIcon));
        }
    }

    /// <summary>True once the icon has loaded; the placeholder binds to the inverse.</summary>
    public bool HasIcon => _icon != null;

    private bool _installing;
    public bool Installing
    {
        get => _installing;
        set { if (_installing != value) { _installing = value; Raise(nameof(Installing)); Raise(nameof(CanInstall)); } }
    }

    private bool _installed;
    /// <summary>True once installed, so the card shows a check instead of the button.</summary>
    public bool Installed
    {
        get => _installed;
        set { if (_installed != value) { _installed = value; Raise(nameof(Installed)); Raise(nameof(CanInstall)); } }
    }

    /// <summary>The install button shows only while idle (not installing, not already installed).</summary>
    public bool CanInstall => !_installing && !_installed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000d:0.#}M",
        >= 1_000 => $"{n / 1_000d:0.#}K",
        _ => n.ToString()
    };
}
