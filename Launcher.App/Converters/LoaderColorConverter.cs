// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Launcher.Core.Models;

namespace Launcher.App.Converters;

/// <summary>
/// Maps a <see cref="LoaderType"/> to a distinct accent brush so each instance card
/// reads at a glance. Subtle, brand-adjacent colours, not loader logos, so the UI
/// stays clean and consistent on the dark theme.
/// </summary>
public sealed class LoaderColorConverter : IValueConverter
{
    private static readonly IBrush Vanilla = new SolidColorBrush(Color.Parse("#6BA368")); // grass green
    private static readonly IBrush Fabric = new SolidColorBrush(Color.Parse("#C8AD7F"));  // loom sand
    private static readonly IBrush Forge = new SolidColorBrush(Color.Parse("#6D8FB3"));   // steel blue
    private static readonly IBrush NeoForge = new SolidColorBrush(Color.Parse("#E4933B")); // amber
    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#9A97A6")); // neutral

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LoaderType loader
            ? loader switch
            {
                LoaderType.Vanilla => Vanilla,
                LoaderType.Fabric => Fabric,
                LoaderType.Forge => Forge,
                LoaderType.NeoForge => NeoForge,
                _ => Fallback
            }
            : Fallback;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
