// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Services;

/// <summary>
/// Mutually-exclusive mod families. Installing one member while another is present
/// breaks the game (same job, different fork), so the UI warns and offers to swap.
/// This is a common-sense guard, not a dependency resolver: add a new incompatible
/// pair by adding a group below — the checking logic never changes.
/// </summary>
public static class ModConflicts
{
    // Each group is a set of equivalents. Members are matched case-insensitively as a
    // substring of the mod's slug or title, so "iris" matches "Iris Shaders".
    private static readonly string[][] Groups =
    {
        new[] { "sodium", "embeddium" },   // rendering optimizer (Fabric / Forge-NeoForge fork)
        new[] { "iris", "oculus" },        // shader loader (Fabric / Forge-NeoForge fork)
    };

    /// <summary>
    /// If installing the mod identified by <paramref name="slug"/>/<paramref name="title"/> would
    /// conflict with something the instance already has, returns
    /// (installedKeyword, incomingKeyword); otherwise null. <paramref name="installedNames"/> are the
    /// normalized names of the content already installed (letters/digits, lower-cased).
    /// </summary>
    public static (string installed, string incoming)? Find(string? slug, string? title, IEnumerable<string> installedNames)
    {
        var incoming = MatchingMembers(slug, title);
        if (incoming.Count == 0) return null;

        var installed = installedNames.ToList();
        foreach (var (gi, member) in AllMembers())
        {
            // A different member of a group the incoming mod belongs to, already installed.
            if (incoming.Any(x => x.group == gi && x.member != member)
                && installed.Any(n => n.Contains(member)))
                return (member, incoming.First(x => x.group == gi).member);
        }
        return null;
    }

    private static List<(int group, string member)> MatchingMembers(string? slug, string? title)
    {
        var hay = ((slug ?? "") + " " + (title ?? "")).ToLowerInvariant();
        var list = new List<(int, string)>();
        for (int gi = 0; gi < Groups.Length; gi++)
            foreach (var m in Groups[gi])
                if (hay.Contains(m)) list.Add((gi, m));
        return list;
    }

    private static IEnumerable<(int group, string member)> AllMembers()
    {
        for (int gi = 0; gi < Groups.Length; gi++)
            foreach (var m in Groups[gi])
                yield return (gi, m);
    }
}
