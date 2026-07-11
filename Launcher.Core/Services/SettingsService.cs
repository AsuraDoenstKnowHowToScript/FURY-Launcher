// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Launcher.Core.Models;

namespace Launcher.Core.Services;

/// <summary>Loads/saves small UI preferences to <c>settings.json</c>. No UI, no network.</summary>
public sealed class SettingsService
{
    private readonly LauncherPaths _paths;

    public SettingsService(LauncherPaths paths) => _paths = paths;

    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default)
        => await JsonStore.ReadAsync<LauncherSettings>(_paths.SettingsFile, ct).ConfigureAwait(false)
           ?? new LauncherSettings();

    public Task SaveAsync(LauncherSettings settings, CancellationToken ct = default)
        => JsonStore.WriteAsync(_paths.SettingsFile, settings, ct);
}
