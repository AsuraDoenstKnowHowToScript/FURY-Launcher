// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Launcher.App.Controls;

/// <summary>
/// The five-bar connection meter from the game's multiplayer list, drawn rather than templated
/// because it is five rectangles and a template would cost more than the drawing does.
///
/// Bars that are not lit stay visible at low opacity instead of disappearing, so the meter has
/// the same width and shape whatever the latency is and a row of servers stays aligned.
/// </summary>
public sealed class PingBars : Control
{
    public static readonly StyledProperty<int> BarsProperty =
        AvaloniaProperty.Register<PingBars, int>(nameof(Bars), 0);

    /// <summary>Lit bars, 0 to 5. Anything outside that range is clamped.</summary>
    public int Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public static readonly StyledProperty<IBrush?> LitBrushProperty =
        AvaloniaProperty.Register<PingBars, IBrush?>(nameof(LitBrush));

    public IBrush? LitBrush
    {
        get => GetValue(LitBrushProperty);
        set => SetValue(LitBrushProperty, value);
    }

    public static readonly StyledProperty<IBrush?> DimBrushProperty =
        AvaloniaProperty.Register<PingBars, IBrush?>(nameof(DimBrush));

    public IBrush? DimBrush
    {
        get => GetValue(DimBrushProperty);
        set => SetValue(DimBrushProperty, value);
    }

    private const int Count = 5;
    private const double BarWidth = 3;
    private const double Gap = 2;

    static PingBars()
    {
        AffectsRender<PingBars>(BarsProperty, LitBrushProperty, DimBrushProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(Count * BarWidth + (Count - 1) * Gap, 14);

    public override void Render(DrawingContext context)
    {
        var lit = LitBrush ?? Brushes.LimeGreen;
        var dim = DimBrush ?? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        var on = Math.Clamp(Bars, 0, Count);

        var height = Bounds.Height;
        for (var i = 0; i < Count; i++)
        {
            // Each bar is taller than the last, so the meter reads as a ramp even in greyscale
            // and not only by how many are coloured in.
            var scale = (i + 1) / (double)Count;
            var barHeight = Math.Max(3, height * scale);
            var rect = new Rect(i * (BarWidth + Gap), height - barHeight, BarWidth, barHeight);
            context.DrawRectangle(i < on ? lit : dim, null, new RoundedRect(rect, 1.5));
        }
    }
}
