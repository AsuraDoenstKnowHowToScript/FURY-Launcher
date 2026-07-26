// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>Where a server is hosted, used only to group the directory.</summary>
public enum ServerRegion
{
    Unknown,
    NorthAmerica,
    Europe,
    Asia,
    SouthAmerica,
    Oceania,
}

/// <summary>How a server got into the list, which decides where it is shown.</summary>
public enum ServerOrigin
{
    /// <summary>Read out of an instance's servers.dat: a server the user actually joined.</summary>
    Played,
    /// <summary>Shipped with the launcher as a starting point.</summary>
    Directory,
    /// <summary>Typed in by the user and kept in servers.json.</summary>
    Pinned,
}

/// <summary>An address to ping, plus whatever we knew about it before pinging.</summary>
public sealed class ServerEntry
{
    /// <summary>Label shown when the server is unreachable and has no MOTD to show instead.</summary>
    public string Name { get; set; } = "";

    /// <summary>Host, optionally with <c>:port</c>. Port defaults to 25565.</summary>
    public string Address { get; set; } = "";

    public ServerRegion Region { get; set; } = ServerRegion.Unknown;

    public ServerOrigin Origin { get; set; } = ServerOrigin.Pinned;

    /// <summary>Icon cached in servers.dat by the game, base64 PNG without a data: prefix.</summary>
    public string? CachedIcon { get; set; }

    /// <summary>Name of the instance this came from, when it was read from a servers.dat.</summary>
    public string? InstanceName { get; set; }
}

/// <summary>
/// One styled span of a MOTD. Minecraft servers describe their MOTD either as legacy text with
/// section-sign codes or as a chat component tree; both are flattened to a list of these so the
/// UI never has to know which one arrived.
/// </summary>
public sealed class MotdRun
{
    public string Text { get; set; } = "";
    /// <summary>#RRGGBB. Never null: unstyled text resolves to the default MOTD grey.</summary>
    public string Color { get; set; } = "#AAAAAA";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
}

/// <summary>The result of a Server List Ping. Always returned, even when the server is down.</summary>
public sealed class ServerStatus
{
    public string Address { get; set; } = "";
    public bool Online { get; set; }

    /// <summary>Why the ping failed, for the tooltip. Null when online.</summary>
    public string? Error { get; set; }

    /// <summary>Round trip of the ping/pong exchange, in milliseconds.</summary>
    public int LatencyMs { get; set; }

    public int PlayersOnline { get; set; }
    public int PlayersMax { get; set; }

    /// <summary>Version string the server reports, e.g. "Paper 1.21" or "Requires MC 1.8-1.21".</summary>
    public string VersionName { get; set; } = "";

    /// <summary>Flattened MOTD. Empty when the server did not send one.</summary>
    public IReadOnlyList<MotdRun> Motd { get; set; } = Array.Empty<MotdRun>();

    /// <summary>Decoded 64x64 server icon, or null when the server has none.</summary>
    public byte[]? Icon { get; set; }

    /// <summary>
    /// Signal strength on Minecraft's own five-bar scale: 5 is a good connection, 1 is barely
    /// usable, 0 means unreachable. The thresholds match what the game's multiplayer list uses.
    /// </summary>
    public int Bars => !Online ? 0
                     : LatencyMs < 150 ? 5
                     : LatencyMs < 300 ? 4
                     : LatencyMs < 600 ? 3
                     : LatencyMs < 1000 ? 2
                     : 1;
}
