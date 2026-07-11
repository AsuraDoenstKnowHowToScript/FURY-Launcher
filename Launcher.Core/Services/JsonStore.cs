// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core.Services;

/// <summary>Tiny async JSON read/write helper over System.Text.Json.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return default;

        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct).ConfigureAwait(false);
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write to a temp file then move, so a crash never leaves a half-written file.
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, value, Options, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
