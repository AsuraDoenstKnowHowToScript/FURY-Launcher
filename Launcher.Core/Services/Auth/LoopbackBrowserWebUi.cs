// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Diagnostics;
using System.Net;
using System.Text;
using XboxAuthNet.OAuth.CodeFlow;

namespace Launcher.Core.Services;

/// <summary>
/// Microsoft OAuth "web UI" that uses the user's real system browser plus a local
/// loopback redirect, instead of an embedded WebView (which this Avalonia app does
/// not ship). The redirect URI is a <c>http://127.0.0.1:{port}/</c> address that a
/// local <see cref="HttpListener"/> catches. This is why the original build did
/// "nothing" on login: the default flow needs an embedded browser we don't have.
/// </summary>
public sealed class LoopbackBrowserWebUi : IWebUI
{
    private readonly string _redirectUri;   // e.g. http://127.0.0.1:49215/
    private readonly Action<string> _log;
    private readonly TimeSpan _timeout;

    public LoopbackBrowserWebUi(string redirectUri, Action<string> log, TimeSpan? timeout = null)
    {
        _redirectUri = redirectUri.EndsWith('/') ? redirectUri : redirectUri + "/";
        _log = log;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(
        Uri uri, ICodeFlowUrlChecker uriChecker, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(_redirectUri);
        // Start the loopback listener BEFORE opening the browser, so we never miss
        // the redirect if the user logs in very quickly.
        listener.Start();
        _log($"[auth] Waiting for Microsoft sign-in on {_redirectUri}");

        OpenBrowser(uri.ToString());

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log("[auth] Sign-in timed out or was cancelled before the browser redirected back.");
            throw;
        }

        var requestUri = context.Request.Url!;
        await RespondAsync(context).ConfigureAwait(false);

        var result = uriChecker.GetAuthCodeResult(requestUri);
        if (!result.IsSuccess)
            _log($"[auth] Sign-in did not return a code (error: {result.Error}; {result.ErrorDescription}).");
        return result;
    }

    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
    {
        // Used e.g. for browser sign-out — just open the page.
        OpenBrowser(uri.ToString());
        return Task.CompletedTask;
    }

    private static async Task RespondAsync(HttpListenerContext context)
    {
        const string html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>FURY Launcher</title></head>" +
            "<body style=\"font-family:sans-serif;text-align:center;margin-top:80px\">" +
            "<h2>FURY Launcher</h2><p>Sign-in complete. You can close this tab and return to the launcher.</p>" +
            "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        try
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch
        {
            // The browser may have closed the socket; the code was already captured.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// Opens the system browser. The critical fix: <see cref="ProcessStartInfo.UseShellExecute"/>
    /// must be true or .NET throws Win32Exception (which was being swallowed). A
    /// <c>cmd /c start</c> fallback covers odd shell configurations.
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
