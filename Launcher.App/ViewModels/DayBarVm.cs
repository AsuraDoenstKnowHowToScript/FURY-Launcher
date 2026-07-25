// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.App.ViewModels;

/// <summary>
/// One column of the seven-day activity strip. The bar is drawn by binding
/// <see cref="BarHeight"/> directly, so the chart needs no plotting library.
/// </summary>
public sealed class DayBarVm
{
    /// <summary>Tallest a column can get, in pixels.</summary>
    public const double MaxHeight = 86;

    /// <summary>Kept visible even at zero, so an empty day still reads as a day.</summary>
    private const double MinHeight = 6;

    public DayBarVm(string label, long seconds, double fraction, bool isToday, string tip)
    {
        Label = label;
        Seconds = seconds;
        IsToday = isToday;
        Tip = tip;
        BarHeight = MinHeight + Math.Clamp(fraction, 0, 1) * (MaxHeight - MinHeight);
    }

    /// <summary>Single-letter weekday initial.</summary>
    public string Label { get; }

    public long Seconds { get; }
    public double BarHeight { get; }

    /// <summary>Today's column is accented instead of muted.</summary>
    public bool IsToday { get; }

    /// <summary>Hover text, e.g. "Tue · 1h 20m".</summary>
    public string Tip { get; }
}
