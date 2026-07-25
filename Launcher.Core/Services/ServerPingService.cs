// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Speaks Minecraft's Server List Ping, the same exchange the game's multiplayer screen makes:
/// connect, handshake into status state, ask for status, then ping for a round trip time. The
/// server answers with its MOTD, player counts, version and icon, so everything the list shows
/// is the server's own live data rather than a third party's index of it.
///
/// Not implemented: SRV record lookup. The game resolves _minecraft._tcp.&lt;host&gt; first, which
/// needs a DNS library .NET does not ship. Servers that publish a plain A record on 25565 (which
/// is nearly all of them) work; one that only publishes SRV needs its port typed in.
/// </summary>
public sealed class ServerPingService
{
    private const int DefaultPort = 25565;

    /// <summary>Status responses are small; anything past this is a server misbehaving.</summary>
    private const int MaxPacketBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Pings one server. Never throws for an unreachable host: a server being down is a normal
    /// result to display, not an error to handle, so it comes back as Online = false.
    /// </summary>
    public async Task<ServerStatus> PingAsync(string address, CancellationToken ct = default)
    {
        var status = new ServerStatus { Address = address };
        var (host, port) = SplitAddress(address);
        if (string.IsNullOrWhiteSpace(host))
        {
            status.Error = "empty address";
            return status;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            await using var net = tcp.GetStream();

            // Handshake. Protocol version -1 is the documented "I am only asking for status"
            // value; the server ignores it in this state, so no version negotiation happens and
            // old and new servers both answer.
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, -1);
            WriteString(handshake, host);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)(port & 0xFF));
            WriteVarInt(handshake, 1); // next state: status
            await SendPacketAsync(net, handshake.ToArray(), cts.Token).ConfigureAwait(false);

            var request = new MemoryStream();
            WriteVarInt(request, 0x00);
            await SendPacketAsync(net, request.ToArray(), cts.Token).ConfigureAwait(false);

            var body = await ReadPacketAsync(net, cts.Token).ConfigureAwait(false);
            var read = new MemoryStream(body);
            var responseId = ReadVarInt(read);
            if (responseId != 0x00) throw new InvalidDataException($"unexpected packet 0x{responseId:X2}");
            var json = ReadString(read);

            Parse(json, status);

            // Latency is measured from the ping/pong exchange rather than from the connect, so it
            // reports the round trip the player will actually feel instead of the handshake cost.
            var pingBody = new MemoryStream();
            WriteVarInt(pingBody, 0x01);
            WriteLong(pingBody, DateTime.UtcNow.Ticks);
            var clock = Stopwatch.StartNew();
            await SendPacketAsync(net, pingBody.ToArray(), cts.Token).ConfigureAwait(false);
            try
            {
                await ReadPacketAsync(net, cts.Token).ConfigureAwait(false);
                status.LatencyMs = (int)clock.ElapsedMilliseconds;
            }
            catch
            {
                // Some proxies close the socket right after the status response. The status is
                // the part that matters, so keep it and fall back to the time it took to arrive.
                status.LatencyMs = (int)clock.ElapsedMilliseconds;
            }

            status.Online = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            status.Error = "timed out";
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }
        return status;
    }

    /// <summary>Pings many servers at once, bounded so a long list cannot open hundreds of sockets.</summary>
    public async Task<IReadOnlyList<ServerStatus>> PingAllAsync(
        IEnumerable<string> addresses, int parallelism = 8, CancellationToken ct = default)
    {
        var list = addresses.ToList();
        var results = new ServerStatus[list.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        var jobs = list.Select(async (addr, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { results[i] = await PingAsync(addr, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(jobs).ConfigureAwait(false);
        return results;
    }

    // ============================ response parsing ============================

    private static void Parse(string json, ServerStatus status)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var on) && on.TryGetInt32(out var onv))
                status.PlayersOnline = onv;
            if (players.TryGetProperty("max", out var max) && max.TryGetInt32(out var maxv))
                status.PlayersMax = maxv;
        }

        if (root.TryGetProperty("version", out var version) &&
            version.TryGetProperty("name", out var vname) && vname.ValueKind == JsonValueKind.String)
            status.VersionName = vname.GetString() ?? "";

        if (root.TryGetProperty("description", out var description))
        {
            var runs = new List<MotdRun>();
            FlattenComponent(description, new MotdRun(), runs);
            status.Motd = runs.Where(r => r.Text.Length > 0).ToList();
        }

        // "data:image/png;base64,...." — the prefix is part of the protocol, not of the image.
        if (root.TryGetProperty("favicon", out var favicon) && favicon.ValueKind == JsonValueKind.String)
        {
            var raw = favicon.GetString() ?? "";
            var comma = raw.IndexOf(',');
            if (comma >= 0) raw = raw[(comma + 1)..];
            try { status.Icon = Convert.FromBase64String(raw.Trim()); }
            catch { status.Icon = null; }
        }
    }

    /// <summary>
    /// Walks a chat component into flat styled runs. Styling inherits down the tree, and any
    /// legacy section-sign codes inside a text node are expanded as their own runs, because
    /// plenty of servers still send a modern component whose text is full of old codes.
    /// </summary>
    private static void FlattenComponent(JsonElement el, MotdRun inherited, List<MotdRun> outp)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                ExpandLegacy(el.GetString() ?? "", inherited, outp);
                return;

            case JsonValueKind.Array:
                foreach (var child in el.EnumerateArray()) FlattenComponent(child, inherited, outp);
                return;

            case JsonValueKind.Object:
                break;

            default:
                return;
        }

        var style = new MotdRun
        {
            Color = inherited.Color,
            Bold = inherited.Bold,
            Italic = inherited.Italic,
            Underline = inherited.Underline,
            Strikethrough = inherited.Strikethrough,
        };

        if (el.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String)
            style.Color = NamedColor(color.GetString() ?? "") ?? style.Color;
        if (el.TryGetProperty("bold", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
            style.Bold = b.GetBoolean();
        if (el.TryGetProperty("italic", out var i) && i.ValueKind is JsonValueKind.True or JsonValueKind.False)
            style.Italic = i.GetBoolean();
        if (el.TryGetProperty("underlined", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False)
            style.Underline = u.GetBoolean();
        if (el.TryGetProperty("strikethrough", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
            style.Strikethrough = s.GetBoolean();

        if (el.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            ExpandLegacy(text.GetString() ?? "", style, outp);

        if (el.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            foreach (var child in extra.EnumerateArray()) FlattenComponent(child, style, outp);
    }

    /// <summary>Splits legacy section-sign text into runs. Unknown codes are dropped, not printed.</summary>
    private static void ExpandLegacy(string text, MotdRun start, List<MotdRun> outp)
    {
        if (text.Length == 0) return;

        var current = Clone(start);
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            current.Text = buffer.ToString();
            outp.Add(current);
            current = Clone(current);
            current.Text = "";
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '§' || i + 1 >= text.Length)
            {
                buffer.Append(text[i]);
                continue;
            }

            var code = char.ToLowerInvariant(text[++i]);
            var hex = CodeColor(code);
            if (hex != null)
            {
                // A colour code also clears the formatting flags, same as in game.
                Flush();
                current.Color = hex;
                current.Bold = current.Italic = current.Underline = current.Strikethrough = false;
                continue;
            }

            switch (code)
            {
                case 'l': Flush(); current.Bold = true; break;
                case 'o': Flush(); current.Italic = true; break;
                case 'n': Flush(); current.Underline = true; break;
                case 'm': Flush(); current.Strikethrough = true; break;
                case 'r':
                    Flush();
                    current.Color = start.Color;
                    current.Bold = current.Italic = current.Underline = current.Strikethrough = false;
                    break;
                // 'k' is the scrambling effect. There is nothing sensible to render for it, and
                // animating it would be the only moving thing on the page, so it is left as text.
                default: break;
            }
        }
        Flush();
    }

    private static MotdRun Clone(MotdRun r) => new()
    {
        Color = r.Color,
        Bold = r.Bold,
        Italic = r.Italic,
        Underline = r.Underline,
        Strikethrough = r.Strikethrough,
    };

    private static string? CodeColor(char c) => c switch
    {
        '0' => "#000000", '1' => "#0000AA", '2' => "#00AA00", '3' => "#00AAAA",
        '4' => "#AA0000", '5' => "#AA00AA", '6' => "#FFAA00", '7' => "#AAAAAA",
        '8' => "#555555", '9' => "#5555FF", 'a' => "#55FF55", 'b' => "#55FFFF",
        'c' => "#FF5555", 'd' => "#FF55FF", 'e' => "#FFFF55", 'f' => "#FFFFFF",
        _ => null,
    };

    private static string? NamedColor(string name)
    {
        if (name.StartsWith('#')) return name; // 1.16+ servers may send a literal hex colour
        return name.ToLowerInvariant() switch
        {
            "black" => "#000000", "dark_blue" => "#0000AA", "dark_green" => "#00AA00",
            "dark_aqua" => "#00AAAA", "dark_red" => "#AA0000", "dark_purple" => "#AA00AA",
            "gold" => "#FFAA00", "gray" or "grey" => "#AAAAAA", "dark_gray" or "dark_grey" => "#555555",
            "blue" => "#5555FF", "green" => "#55FF55", "aqua" => "#55FFFF", "red" => "#FF5555",
            "light_purple" => "#FF55FF", "yellow" => "#FFFF55", "white" => "#FFFFFF",
            _ => null,
        };
    }

    // ============================ wire format ============================

    public static (string Host, int Port) SplitAddress(string address)
    {
        var raw = (address ?? "").Trim();
        if (raw.Length == 0) return ("", DefaultPort);

        // Only split on the last colon, so an IPv6 literal in brackets survives.
        if (raw.StartsWith('['))
        {
            var close = raw.IndexOf(']');
            if (close > 0)
            {
                var v6 = raw[1..close];
                var rest = raw[(close + 1)..];
                return rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)
                    ? (v6, p6) : (v6, DefaultPort);
            }
        }

        var colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var port) && port is > 0 and <= 65535)
            return (raw[..colon], port);
        return (raw, DefaultPort);
    }

    private static async Task SendPacketAsync(Stream s, byte[] body, CancellationToken ct)
    {
        var framed = new MemoryStream();
        WriteVarInt(framed, body.Length);
        framed.Write(body, 0, body.Length);
        var bytes = framed.ToArray();
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(s, ct).ConfigureAwait(false);
        if (length is <= 0 or > MaxPacketBytes) throw new InvalidDataException($"bad packet length {length}");
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("connection closed mid-packet");
            read += n;
        }
        return buffer;
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = unchecked((uint)value);
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    private static int ReadVarInt(Stream s)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("varint too long");
    }

    private static async Task<int> ReadVarIntAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            result |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return result;
        }
        throw new InvalidDataException("varint too long");
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string ReadString(Stream s)
    {
        var length = ReadVarInt(s);
        if (length is < 0 or > MaxPacketBytes) throw new InvalidDataException($"bad string length {length}");
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = s.Read(buffer, read, length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }

    private static void WriteLong(Stream s, long value)
    {
        for (var i = 7; i >= 0; i--) s.WriteByte((byte)(value >> (i * 8)));
    }
}
