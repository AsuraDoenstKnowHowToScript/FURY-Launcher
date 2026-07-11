// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace Launcher.Core.Services;

/// <summary>
/// Authentication: offline (deterministic UUID by name) and Microsoft Xbox
/// (OAuth via CmlLib's <see cref="JELoginHandler"/>). Microsoft sessions are
/// cached by CmlLib's account manager under the launcher data folder so the
/// user does not re-login every time.
/// </summary>
public interface IAuthManager
{
    MSession CreateOffline(string username);
    Task<MSession> LoginMicrosoftAsync(CancellationToken ct = default);
    Task<MSession?> TryResumeMicrosoftAsync(CancellationToken ct = default);
}

public sealed class AuthManager : IAuthManager
{
    private readonly LauncherPaths _paths;
    private readonly Lazy<JELoginHandler> _handler;

    public AuthManager(LauncherPaths paths)
    {
        _paths = paths;
        _handler = new Lazy<JELoginHandler>(() =>
            new JELoginHandlerBuilder()
                .WithAccountManager(_paths.AccountsFile)
                .Build());
    }

    /// <summary>Offline session with the standard name-derived UUID.</summary>
    public MSession CreateOffline(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty.", nameof(username));

        var session = MSession.CreateOfflineSession(username.Trim());
        session.UUID = OfflineIdentity.Uuid(username.Trim());
        return session;
    }

    /// <summary>
    /// Full Microsoft login: tries the cached session first, then falls back to
    /// interactive OAuth (opens the system browser). Throws on failure — never
    /// swallowed.
    /// </summary>
    public async Task<MSession> LoginMicrosoftAsync(CancellationToken ct = default)
    {
        try
        {
            return await _handler.Value.AuthenticateSilently().ConfigureAwait(false);
        }
        catch
        {
            // No valid cached session — do the interactive flow.
            return await _handler.Value.AuthenticateInteractively().ConfigureAwait(false);
        }
    }

    /// <summary>Silent-only login for app startup; returns null when no cached session exists.</summary>
    public async Task<MSession?> TryResumeMicrosoftAsync(CancellationToken ct = default)
    {
        try
        {
            return await _handler.Value.AuthenticateSilently().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
