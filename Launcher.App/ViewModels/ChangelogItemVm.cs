// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Services;

namespace Launcher.App.ViewModels;

/// <summary>
/// One release in the in-app changelog. The GitHub body is markdown; rather than pulling in a
/// renderer we flatten it to plain lines (headings become their own line, bullets keep a dash),
/// which is all a compact panel needs.
/// </summary>
public sealed class ChangelogItemVm
{
    public ChangelogItemVm(ReleaseNote note, bool isCurrent)
    {
        Version = note.Version;
        IsBeta = note.IsBeta;
        HtmlUrl = note.HtmlUrl;
        IsCurrent = isCurrent;
        DateText = note.PublishedUtc == default ? "" : note.PublishedUtc.ToLocalTime().ToString("dd MMM yyyy");
        Summary = Flatten(note.Body);
        Channel = note.IsBeta ? "BETA" : "STABLE";

        // The item opens its own release page, so the template needs no parent lookup.
        OpenCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(HtmlUrl)) return;
            try { Process.Start(new ProcessStartInfo(HtmlUrl) { UseShellExecute = true }); }
            catch (Exception ex) { CrashLog.Write("[changelog] opening the release page failed", ex); }
        });
    }

    public IRelayCommand OpenCommand { get; }

    /// <summary>Marker text for the build that is running, resolved per item so the template
    /// never has to reach out to the parent view model.</summary>
    public string CurrentLabel => Launcher.Core.Localization.Loc.T("dash.current");

    public string Version { get; }
    public string Channel { get; }
    public bool IsBeta { get; }
    public string DateText { get; }
    public string Summary { get; }
    public string HtmlUrl { get; }

    /// <summary>True for the build that is running right now.</summary>
    public bool IsCurrent { get; }

    /// <summary>Strips markdown decoration down to readable lines, capped so a card stays a card.</summary>
    private static string Flatten(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var lines = body.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.TrimStart('#', ' ').Trim())
            .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "· " + l[2..].Trim() : l)
            .Select(l => l.Replace("**", "").Replace("`", ""))
            .Take(10)
            .ToArray();

        return string.Join("\n", lines);
    }
}
