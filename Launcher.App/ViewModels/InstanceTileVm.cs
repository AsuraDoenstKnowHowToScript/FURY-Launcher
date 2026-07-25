// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.App.ViewModels;

/// <summary>
/// One instance on the dashboard: what it is, how long it was played, and how big a slice
/// of the user's total time that is (<see cref="Share"/> drives the bar). Immutable — the
/// dashboard rebuilds the list on refresh.
/// </summary>
public sealed class InstanceTileVm
{
    public InstanceTileVm(Instance instance, long seconds, double share, string playedText, string lastPlayedText)
    {
        Instance = instance;
        Seconds = seconds;
        Share = share;
        PlayedText = playedText;
        LastPlayedText = lastPlayedText;
    }

    public Instance Instance { get; }
    public string Id => Instance.Id;
    public string Name => Instance.Name;
    public LoaderType Loader => Instance.Loader;

    /// <summary>"Forge · 1.20.1" — the technical line under the name.</summary>
    public string Meta => $"{Instance.Loader} · {Instance.McVersion}";

    public long Seconds { get; }

    /// <summary>0..1 of the busiest instance's time, so the longest bar is always full.</summary>
    public double Share { get; }

    public string PlayedText { get; }
    public string LastPlayedText { get; }

    /// <summary>True once the instance has actually been played, so the UI can mute the rest.</summary>
    public bool HasPlaytime => Seconds > 0;
}
