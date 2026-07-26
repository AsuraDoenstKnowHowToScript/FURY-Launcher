// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Launcher.Core.Models;

namespace Launcher.App.Converters;

/// <summary>
/// True when the bound <see cref="LoaderType"/> equals the name passed as the converter
/// parameter. Used to pick which loader glyph to show on an instance card: each candidate icon
/// is declared with the icon pack's markup extension and toggled by this test, which keeps the
/// icons resolving through the normal XAML path instead of being built in code.
/// </summary>
public sealed class LoaderIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LoaderType loader
           && parameter is string name
           && string.Equals(loader.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
