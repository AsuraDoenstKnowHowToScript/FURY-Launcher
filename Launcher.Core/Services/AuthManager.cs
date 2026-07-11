// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using XboxAuthNet.OAuth.CodeFlow;

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
    Task SignOutMicrosoftAsync(CancellationToken ct = default);
}

public sealed class AuthManager : IAuthManager
{
    private readonly LauncherPaths _paths;
    private readonly Lazy<JELoginHandler> _handler;

    /// <summary>Diagnostic lines from the auth flow (browser open, OAuth errors, ...).</summary>
    public event EventHandler<string>? Log;

    /// <summary>
    /// Set by the app: builds the embedded WebView login window used for the interactive
    /// Microsoft sign-in. When null (or it returns null), we fall back to the paste flow
    /// (<see cref="InteractivePrompt"/>).
    /// </summary>
    public Func<IWebUI?>? WebUiFactory { get; set; }

    /// <summary>
    /// Paste fallback used only when no embedded WebView is available: shows a dialog
    /// asking the user to paste the redirected URL, returning it (null/empty = cancelled).
    /// </summary>
    public Func<Uri, CancellationToken, Task<string?>>? InteractivePrompt { get; set; }

    public AuthManager(LauncherPaths paths)
    {
        _paths = paths;
        void LogLine(string msg) { Log?.Invoke(this, msg); CrashLog.Write(msg); }
        _handler = new Lazy<JELoginHandler>(() =>
            new JELoginHandlerBuilder()
                // Interactive step uses the app's embedded WebView; the CmlLib default
                // needs an embedded browser we wire up ourselves. Paste is the fallback.
                .WithOAuthProvider(new SystemBrowserOAuthProvider(
                    JELoginHandler.DefaultMicrosoftOAuthClientInfo,
                    () => WebUiFactory?.Invoke()
                          ?? new SystemBrowserWebUi(LogLine, (uri, ct) =>
                              InteractivePrompt is { } prompt ? prompt(uri, ct) : Task.FromResult<string?>(null))))
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

    /// <summary>
    /// Signs out of the cached Microsoft account (clears the local tokens so the next
    /// login starts fresh). Best-effort: if the handler has no account, we still wipe
    /// the accounts cache file so nothing is left behind.
    /// </summary>
    public async Task SignOutMicrosoftAsync(CancellationToken ct = default)
    {
        try
        {
            await _handler.Value.Signout(ct).ConfigureAwait(false);
            Log?.Invoke(this, "[auth] Signed out of the Microsoft account.");
        }
        catch (Exception ex)
        {
            CrashLog.Write("[auth] signout via handler failed; wiping accounts cache", ex);
            try
            {
                if (File.Exists(_paths.AccountsFile)) File.Delete(_paths.AccountsFile);
                Log?.Invoke(this, "[auth] Signed out (cleared local account cache).");
            }
            catch (Exception ex2)
            {
                Log?.Invoke(this, "[auth] Could not fully sign out: " + ex2.Message);
                CrashLog.Write("[auth] deleting accounts cache failed", ex2);
            }
        }
    }
}
