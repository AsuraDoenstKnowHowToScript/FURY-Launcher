// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
        Lines = Parse(note.Body);
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

    /// <summary>The release body, parsed into renderable blocks.</summary>
    public IReadOnlyList<ChangelogLineVm> Lines { get; }

    public string HtmlUrl { get; }

    /// <summary>True for the build that is running right now.</summary>
    public bool IsCurrent { get; }

    /// <summary>How many blocks a card shows before the rest is left to the release page.</summary>
    private const int MaxBlocks = 12;

    /// <summary>
    /// Turns a markdown release body into display blocks. The important part is rejoining the
    /// hard wraps: GitHub bodies break mid-sentence at ~72 columns, and printing those raw is
    /// what makes a changelog look shredded. Consecutive plain lines become one paragraph;
    /// headings and list items keep their own identity.
    /// </summary>
    private static IReadOnlyList<ChangelogLineVm> Parse(string body)
    {
        var blocks = new List<ChangelogLineVm>();
        if (string.IsNullOrWhiteSpace(body)) return blocks;

        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new ChangelogLineVm(Clean(paragraph.ToString()), isHeading: false, isBullet: false));
            paragraph.Clear();
        }

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0) { FlushParagraph(); continue; }

            if (line.StartsWith('#'))
            {
                FlushParagraph();
                blocks.Add(new ChangelogLineVm(Clean(line.TrimStart('#', ' ')), isHeading: true, isBullet: false));
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushParagraph();
                blocks.Add(new ChangelogLineVm(Clean(line[2..]), isHeading: false, isBullet: true));
            }
            else
            {
                // A wrapped continuation of the current paragraph.
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }

            if (blocks.Count >= MaxBlocks) break;
        }
        FlushParagraph();

        return blocks.Count > MaxBlocks ? blocks.Take(MaxBlocks).ToList() : blocks;
    }

    /// <summary>Drops the markdown decoration that has no meaning once rendered as plain text.</summary>
    private static string Clean(string s)
        => s.Replace("**", "").Replace("`", "").Replace("__", "").Trim();
}
