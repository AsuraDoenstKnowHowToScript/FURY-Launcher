// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Launcher.App.Controls;

/// <summary>
/// A section panel with weight: a dot grid, a violet glow bleeding in from the top corner and
/// an oversized watermark of the section's own icon, all under the content. The point is that a
/// panel reads as a surface rather than a plain rectangle, and that every section gets the same
/// treatment from one declaration:
/// <code>&lt;ctl:PanelCard Header="Java" Icon="{icon:LucideImage Kind=Coffee, ...}"&gt;</code>
/// The icon is used twice — small in the header chip, huge and faint as the watermark — so a
/// section only ever states its identity once.
/// </summary>
public class PanelCard : ContentControl
{
    // Without this the control would look for a ContentControl theme and lose its template.
    protected override Type StyleKeyOverride => typeof(PanelCard);

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<PanelCard, string?>(nameof(Header));

    /// <summary>Section title. Leave empty for a panel with no header row.</summary>
    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<PanelCard, IImage?>(nameof(Icon));

    /// <summary>Section icon: shown in the header chip and, blown up and faint, as the watermark.</summary>
    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly StyledProperty<bool> ShowHeaderProperty =
        AvaloniaProperty.Register<PanelCard, bool>(nameof(ShowHeader), defaultValue: true);

    /// <summary>Set false to keep the texture but drop the title row.</summary>
    public bool ShowHeader
    {
        get => GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public static readonly StyledProperty<double> WatermarkSizeProperty =
        AvaloniaProperty.Register<PanelCard, double>(nameof(WatermarkSize), defaultValue: 168d);

    /// <summary>How large the background watermark is drawn.</summary>
    public double WatermarkSize
    {
        get => GetValue(WatermarkSizeProperty);
        set => SetValue(WatermarkSizeProperty, value);
    }

    public static readonly StyledProperty<double> TextureOpacityProperty =
        AvaloniaProperty.Register<PanelCard, double>(nameof(TextureOpacity), defaultValue: 1d);

    /// <summary>Global dial for the dot grid + glow, for panels that need to stay quieter.</summary>
    public double TextureOpacity
    {
        get => GetValue(TextureOpacityProperty);
        set => SetValue(TextureOpacityProperty, value);
    }
}
