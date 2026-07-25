// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.IO.Compression;
using System.Text;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>
/// Reads the multiplayer list the game itself keeps, <c>.minecraft/servers.dat</c>, so "your
/// servers" means the ones actually joined in this launcher rather than a list kept in parallel
/// that would drift the moment someone added a server from inside the game.
///
/// The file is NBT: a tagged, big-endian tree. Only the handful of fields the multiplayer screen
/// stores are pulled out; every other tag is skipped by walking its length, which is why unknown
/// or future fields do not break the read.
/// </summary>
public static class ServersDatReader
{
    private const int MaxEntries = 500;

    /// <summary>
    /// Reads one servers.dat. Returns an empty list for a missing or unreadable file: a corrupt
    /// or half-written list is not worth failing a screen over.
    /// </summary>
    public static IReadOnlyList<ServerEntry> Read(string path, string? instanceName = null)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<ServerEntry>();
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 3) return Array.Empty<ServerEntry>();

            // Vanilla writes this one uncompressed, unlike level.dat. Sniff anyway, because some
            // third-party tools gzip it and the cost of checking is two bytes.
            if (bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var raw = new MemoryStream(bytes);
                using var gz = new GZipStream(raw, CompressionMode.Decompress);
                using var outp = new MemoryStream();
                gz.CopyTo(outp);
                bytes = outp.ToArray();
            }

            var s = new MemoryStream(bytes);
            if (s.ReadByte() != 10) return Array.Empty<ServerEntry>(); // root must be a compound
            SkipName(s);
            return ReadRootCompound(s, instanceName);
        }
        catch
        {
            return Array.Empty<ServerEntry>();
        }
    }

    private static IReadOnlyList<ServerEntry> ReadRootCompound(Stream s, string? instanceName)
    {
        var result = new List<ServerEntry>();
        while (true)
        {
            var type = s.ReadByte();
            if (type <= 0) return result; // TAG_End, or the stream ran out
            var name = ReadName(s);
            if (type == 9 && name == "servers")
            {
                ReadServerList(s, result, instanceName);
                continue;
            }
            SkipPayload(s, (byte)type);
        }
    }

    private static void ReadServerList(Stream s, List<ServerEntry> result, string? instanceName)
    {
        var elementType = s.ReadByte();
        var count = ReadInt(s);
        if (elementType != 10 || count <= 0) return;

        for (var i = 0; i < count && i < MaxEntries; i++)
        {
            var entry = new ServerEntry { Origin = ServerOrigin.Played, InstanceName = instanceName };
            while (true)
            {
                var type = s.ReadByte();
                if (type <= 0) break; // end of this server's compound
                var field = ReadName(s);
                if (type == 8)
                {
                    var value = ReadString(s);
                    switch (field)
                    {
                        case "ip": entry.Address = value; break;
                        case "name": entry.Name = value; break;
                        case "icon": entry.CachedIcon = value; break;
                    }
                    continue;
                }
                SkipPayload(s, (byte)type);
            }
            if (!string.IsNullOrWhiteSpace(entry.Address)) result.Add(entry);
        }
    }

    // ============================ NBT primitives ============================

    private static void SkipName(Stream s) => ReadName(s);

    private static string ReadName(Stream s)
    {
        var length = ReadUShort(s);
        return ReadUtf8(s, length);
    }

    private static string ReadString(Stream s)
    {
        var length = ReadUShort(s);
        return ReadUtf8(s, length);
    }

    private static string ReadUtf8(Stream s, int length)
    {
        if (length <= 0) return "";
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

    private static int ReadUShort(Stream s)
    {
        var a = s.ReadByte();
        var b = s.ReadByte();
        if (a < 0 || b < 0) throw new EndOfStreamException();
        return (a << 8) | b;
    }

    private static int ReadInt(Stream s)
    {
        var v = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            v = (v << 8) | b;
        }
        return v;
    }

    private static void Skip(Stream s, long count)
    {
        for (var i = 0L; i < count; i++)
            if (s.ReadByte() < 0) throw new EndOfStreamException();
    }

    /// <summary>
    /// Steps over one tag's payload. Every branch has to be right even for tags we never read,
    /// because getting a length wrong here desynchronises everything after it.
    /// </summary>
    private static void SkipPayload(Stream s, byte type)
    {
        switch (type)
        {
            case 1: Skip(s, 1); break;                       // byte
            case 2: Skip(s, 2); break;                       // short
            case 3: case 5: Skip(s, 4); break;               // int, float
            case 4: case 6: Skip(s, 8); break;               // long, double
            case 7: Skip(s, ReadInt(s)); break;              // byte array
            case 8: Skip(s, ReadUShort(s)); break;           // string
            case 9:                                          // list
            {
                var elementType = s.ReadByte();
                var count = ReadInt(s);
                if (elementType < 0) throw new EndOfStreamException();
                for (var i = 0; i < count; i++) SkipPayload(s, (byte)elementType);
                break;
            }
            case 10:                                         // compound
                while (true)
                {
                    var child = s.ReadByte();
                    if (child <= 0) break;
                    SkipName(s);
                    SkipPayload(s, (byte)child);
                }
                break;
            case 11: Skip(s, 4L * ReadInt(s)); break;        // int array
            case 12: Skip(s, 8L * ReadInt(s)); break;        // long array
            default: throw new InvalidDataException($"unknown NBT tag {type}");
        }
    }
}
