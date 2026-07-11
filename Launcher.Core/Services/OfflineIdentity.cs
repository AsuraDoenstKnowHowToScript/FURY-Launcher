// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core.Services;

/// <summary>
/// Computes the standard Minecraft offline UUID, deterministically derived from
/// the username (name-based UUIDv3 of <c>"OfflinePlayer:&lt;name&gt;"</c>), matching
/// the vanilla server scheme.
/// </summary>
public static class OfflineIdentity
{
    public static string Uuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        // Set the version (3) and IETF variant bits, as required for a v3 UUID.
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);

        // Minecraft sessions use the dashless 32-char hex form.
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
