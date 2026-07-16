// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Diagnostics;
using XboxAuthNet.OAuth.CodeFlow;

namespace Launcher.Core.Services;

/// <summary>
/// Microsoft OAuth "web UI" for an app with no embedded WebView. The Minecraft MSA
/// client only accepts the <c>login.live.com/oauth20_desktop.srf</c> redirect (a
/// loopback <c>http://127.0.0.1:port</c> is rejected with "redirect_uri is not
/// valid"), and a system browser can't be intercepted. So we open the system browser
/// on the default redirect and ask the user to paste the final page URL back. The
/// code is in that URL, and the token exchange uses the same registered redirect.
/// </summary>
public sealed class SystemBrowserWebUi : IWebUI
{
    private const string DesktopRedirect = "https://login.live.com/oauth20_desktop.srf";

    private readonly Action<string> _log;
    private readonly Func<Uri, CancellationToken, Task<string?>> _promptForResponse;

    /// <param name="promptForResponse">
    /// Shows UI asking the user to paste the redirected URL (or code); returns it, or
    /// null/empty if cancelled. Supplied by the app layer.
    /// </param>
    public SystemBrowserWebUi(Action<string> log, Func<Uri, CancellationToken, Task<string?>> promptForResponse)
    {
        _log = log;
        _promptForResponse = promptForResponse;
    }

    public async Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(
        Uri uri, ICodeFlowUrlChecker uriChecker, CancellationToken cancellationToken)
    {
        OpenBrowser(uri.ToString());
        _log("[auth] Browser opened for Microsoft sign-in. Paste the final page URL back into the launcher.");

        var pasted = await _promptForResponse(uri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pasted))
        {
            _log("[auth] Sign-in cancelled (nothing pasted).");
            throw new OperationCanceledException("Microsoft sign-in was cancelled.");
        }

        var responseUri = ToResponseUri(pasted.Trim());
        var result = uriChecker.GetAuthCodeResult(responseUri);
        if (result.IsSuccess)
            _log("[auth] Authorization code received; completing sign-in...");
        else
            _log($"[auth] The pasted URL had no valid code (error: {result.Error}; {result.ErrorDescription}).");
        return result;
    }

    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
    {
        // Used e.g. for browser sign-out: just open the page.
        OpenBrowser(uri.ToString());
        return Task.CompletedTask;
    }

    /// <summary>Accepts a full redirect URL (preferred) or a bare code and normalizes it.</summary>
    private static Uri ToResponseUri(string pasted)
    {
        if (pasted.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pasted.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(pasted);

        var query = pasted.TrimStart('?');
        if (!query.Contains('=')) query = "code=" + Uri.EscapeDataString(query);
        return new Uri($"{DesktopRedirect}?{query}");
    }

    /// <summary>
    /// Opens the system browser. UseShellExecute must be true or .NET throws
    /// Win32Exception; a <c>cmd /c start</c> fallback covers odd shell setups.
    /// </summary>
    private void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            _log("[auth] Opened the system browser for Microsoft sign-in.");
            return;
        }
        catch (Exception ex)
        {
            _log($"[auth] Could not open the browser directly ({ex.Message}); trying fallback.");
            CrashLog.Write("[auth] Process.Start(browser) failed", ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
            _log("[auth] Opened the browser via the fallback launcher.");
        }
        catch (Exception ex2)
        {
            _log($"[auth] Failed to open a browser for sign-in: {ex2.Message}");
            CrashLog.Write("[auth] fallback browser open failed", ex2);
        }
    }
}
