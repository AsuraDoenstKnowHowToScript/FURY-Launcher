// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Launcher.Core.Models;

namespace Launcher.App.Controls;

/// <summary>
/// Renders a server's MOTD with the server's own colours and formatting, the way the game's
/// multiplayer list shows it. A MOTD is a styled string, so it becomes a TextBlock of inline
/// runs rather than one flat label: a server that colours half its name loses its identity if
/// that gets thrown away.
///
/// The colours come off the wire, not from the launcher's palette. They are deliberately exempt
/// from the design tokens, because a MOTD in the launcher's violet would be the launcher's MOTD.
/// </summary>
public sealed class MotdBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<MotdRun>?> RunsProperty =
        AvaloniaProperty.Register<MotdBlock, IReadOnlyList<MotdRun>?>(nameof(Runs));

    public IReadOnlyList<MotdRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RunsProperty) Rebuild();
    }

    private void Rebuild()
    {
        var runs = Runs;
        Inlines?.Clear();
        if (runs == null || runs.Count == 0)
        {
            Text = "";
            return;
        }

        // Setting Inlines while Text holds a value shows both, so the plain text is cleared first.
        Text = null;
        var inlines = Inlines ??= new InlineCollection();
        foreach (var run in runs)
        {
            var inline = new Run(run.Text)
            {
                Foreground = Parse(run.Color),
                FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
            };
            if (run.Underline && run.Strikethrough)
                inline.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration { Location = TextDecorationLocation.Underline },
                    new TextDecoration { Location = TextDecorationLocation.Strikethrough },
                };
            else if (run.Underline)
                inline.TextDecorations = TextDecorations.Underline;
            else if (run.Strikethrough)
                inline.TextDecorations = TextDecorations.Strikethrough;

            inlines.Add(inline);
        }
    }

    /// <summary>#RRGGBB from the server. A malformed value falls back to the MOTD grey.</summary>
    private static IBrush Parse(string hex)
    {
        var value = (hex ?? "").TrimStart('#');
        if (value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    }
}
