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

    /// <summary>Diagnostic lines from the auth flow (browser open, OAuth errors, ...).</summary>
    public event EventHandler<string>? Log;

    /// <summary>
    /// Set by the UI: shows a dialog asking the user to paste the redirected URL after
    /// the system browser sign-in, and returns it (null/empty = cancelled). Required for
    /// interactive Microsoft login since this app has no embedded browser.
    /// </summary>
    public Func<Uri, CancellationToken, Task<string?>>? InteractivePrompt { get; set; }

    public AuthManager(LauncherPaths paths)
    {
        _paths = paths;
        _handler = new Lazy<JELoginHandler>(() =>
            new JELoginHandlerBuilder()
                // Use our own system-browser web UI for the interactive step; the default
                // relies on an embedded WebView this app doesn't ship.
                .WithOAuthProvider(new SystemBrowserOAuthProvider(
                    JELoginHandler.DefaultMicrosoftOAuthClientInfo,
                    msg => { Log?.Invoke(this, msg); CrashLog.Write(msg); },
                    (uri, ct) => InteractivePrompt is { } prompt
                        ? prompt(uri, ct)
                        : Task.FromResult<string?>(null)))
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
            return await _handler.Value.AuthenticateSilently(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // No valid cached session — do the interactive flow. Never silent: log why.
            Log?.Invoke(this, $"[auth] No cached session ({ex.Message}); starting interactive sign-in.");
            CrashLog.Write("[auth] silent login failed", ex);
            return await _handler.Value.AuthenticateInteractively(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Silent-only login for app startup; returns null when no cached session exists.</summary>
    public async Task<MSession?> TryResumeMicrosoftAsync(CancellationToken ct = default)
    {
        try
        {
            return await _handler.Value.AuthenticateSilently(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Expected when there is no cached session; log at low volume, don't surface.
            CrashLog.Write("[auth] resume (silent) found no session", ex);
            return null;
        }
    }
}
