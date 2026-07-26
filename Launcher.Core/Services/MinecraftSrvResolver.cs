// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Core.Services;

/// <summary>
/// Resolves the <c>_minecraft._tcp</c> SRV record for a host, the lookup the game does before it
/// connects. Without it a large share of servers look dead: their public domain points at a CDN
/// or a web host, and the address that actually speaks Minecraft is published only through SRV.
/// hylex.gg answers on pirata.hylex.gg:25594 and mushmc.com on br.mush.com.br, while both A
/// records point at Cloudflare, which is why connecting straight to the domain on 25565 times out.
///
/// .NET has no SRV query, so this is a small DNS client: one UDP question, one answer parsed.
/// Every failure path returns null and the caller falls back to the plain host and port, so the
/// worst case is the behaviour we had before rather than a broken lookup.
/// </summary>
public static class MinecraftSrvResolver
{
    private const int DnsPort = 53;
    private const ushort TypeSrv = 33;
    private const ushort ClassIn = 1;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Public resolvers, used only when the machine reports no DNS server of its own.</summary>
    private static readonly IPAddress[] Fallback =
    {
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
    };

    /// <summary>
    /// The host and port to actually connect to, or null when there is no SRV record and the
    /// caller should use the address as typed.
    /// </summary>
    public static async Task<(string Host, int Port)?> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        // An IP literal has nothing to look up.
        if (IPAddress.TryParse(host, out _)) return null;

        var question = "_minecraft._tcp." + host.Trim().TrimEnd('.');
        foreach (var server in Servers())
        {
            try
            {
                var answer = await QueryAsync(server, question, ct).ConfigureAwait(false);
                if (answer != null) return answer;
            }
            catch
            {
                // Try the next resolver; a single unreachable one must not decide the outcome.
            }
        }
        return null;
    }

    /// <summary>The machine's own resolvers first, since they answer split-horizon names too.</summary>
    private static IEnumerable<IPAddress> Servers()
    {
        var seen = new HashSet<IPAddress>();
        // Materialised inside the try on purpose. GetIPProperties can throw on an adapter that
        // disappears mid-enumeration, and a lazy query would raise that outside the catch.
        List<IPAddress> configured;
        try
        {
            configured = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch
        {
            configured = new List<IPAddress>();
        }

        foreach (var a in configured.Concat(Fallback))
            if (seen.Add(a)) yield return a;
    }

    private static async Task<(string Host, int Port)?> QueryAsync(
        IPAddress server, string question, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(QueryTimeout);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPEndPoint(server, DnsPort));

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(id, question);
        await udp.SendAsync(query, cts.Token).ConfigureAwait(false);

        var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        return ParseAnswer(result.Buffer, id);
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var buffer = new List<byte>(64);
        void U16(int v) { buffer.Add((byte)(v >> 8)); buffer.Add((byte)(v & 0xFF)); }

        U16(id);
        U16(0x0100); // standard query, recursion desired
        U16(1);      // one question
        U16(0); U16(0); U16(0);

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new FormatException("bad DNS label");
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);

        U16(TypeSrv);
        U16(ClassIn);
        return buffer.ToArray();
    }

    /// <summary>
    /// Picks the record the game would: lowest priority wins, and among equals the heavier
    /// weight. Returns null for a truncated, mismatched or empty answer.
    /// </summary>
    private static (string Host, int Port)? ParseAnswer(byte[] buffer, ushort expectedId)
    {
        if (buffer.Length < 12) return null;
        if ((ushort)((buffer[0] << 8) | buffer[1]) != expectedId) return null;
        if ((buffer[3] & 0x0F) != 0) return null; // RCODE: NXDOMAIN and friends

        var questions = (buffer[4] << 8) | buffer[5];
        var answers = (buffer[6] << 8) | buffer[7];
        if (answers == 0) return null;

        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!SkipName(buffer, ref offset)) return null;
            offset += 4; // QTYPE + QCLASS
        }

        (int Priority, int Weight, string Host, int Port)? best = null;
        for (var i = 0; i < answers; i++)
        {
            if (!SkipName(buffer, ref offset)) return null;
            if (offset + 10 > buffer.Length) return null;

            var type = (buffer[offset] << 8) | buffer[offset + 1];
            var length = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;
            var next = offset + length;
            if (next > buffer.Length) return null;

            if (type == TypeSrv && length >= 7)
            {
                var priority = (buffer[offset] << 8) | buffer[offset + 1];
                var weight = (buffer[offset + 2] << 8) | buffer[offset + 3];
                var port = (buffer[offset + 4] << 8) | buffer[offset + 5];
                var target = offset + 6;
                var host = ReadName(buffer, ref target);

                // "." means the service is explicitly not offered here.
                if (!string.IsNullOrEmpty(host) && port > 0 &&
                    (best is null || priority < best.Value.Priority ||
                     (priority == best.Value.Priority && weight > best.Value.Weight)))
                {
                    best = (priority, weight, host, port);
                }
            }
            offset = next;
        }

        return best is null ? null : (best.Value.Host, best.Value.Port);
    }

    /// <summary>Reads a name, following compression pointers. Advances past the name in place.</summary>
    private static string ReadName(byte[] buffer, ref int offset)
    {
        var parts = new List<string>();
        var jumped = false;
        var resume = offset;
        // A malformed answer can point a name at itself; the budget is what stops the loop.
        var budget = buffer.Length;

        while (budget-- > 0)
        {
            if (offset >= buffer.Length) break;
            var length = buffer[offset];

            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= buffer.Length) break;
                var pointer = ((length & 0x3F) << 8) | buffer[offset + 1];
                if (!jumped) resume = offset + 2;
                jumped = true;
                offset = pointer;
                continue;
            }

            offset++;
            if (length == 0) break;
            if (offset + length > buffer.Length) break;
            parts.Add(Encoding.ASCII.GetString(buffer, offset, length));
            offset += length;
        }

        if (jumped) offset = resume;
        return string.Join('.', parts);
    }

    private static bool SkipName(byte[] buffer, ref int offset)
    {
        var budget = buffer.Length;
        while (budget-- > 0)
        {
            if (offset >= buffer.Length) return false;
            var length = buffer[offset];
            if ((length & 0xC0) == 0xC0) { offset += 2; return true; }
            offset++;
            if (length == 0) return true;
            offset += length;
        }
        return false;
    }
}
