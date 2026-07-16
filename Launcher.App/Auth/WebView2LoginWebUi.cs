// FURY Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. Do not use, copy, modify or distribute without written
// permission. See the LICENSE file.
// "FURY" is a trademark of the holder. Not affiliated with Mojang/Microsoft.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using XboxAuthNet.OAuth.CodeFlow;

namespace Launcher.App;

/// <summary>
/// Embedded Microsoft sign-in: hosts a WebView2 in a small WinForms window on its own
/// STA thread, navigates to the OAuth page, and captures the authorization code the
/// moment the browser reaches the registered <c>oauth20_desktop.srf</c> redirect, with no
/// system browser tab and no copy/paste. Requires the WebView2 runtime (present on
/// Windows 10/11 by default); callers should check <see cref="IsRuntimeAvailable"/>.
/// </summary>
public sealed class WebView2LoginWebUi : IWebUI
{
    private const string DesktopRedirect = "https://login.live.com/oauth20_desktop.srf";

    private readonly Action<string> _log;
    private readonly string _userDataFolder;

    public WebView2LoginWebUi(Action<string> log, string userDataFolder)
    {
        _log = log;
        _userDataFolder = userDataFolder;
    }

    /// <summary>True when the WebView2 runtime is installed and usable.</summary>
    public static bool IsRuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(
        Uri uri, ICodeFlowUrlChecker uriChecker, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CodeFlowAuthorizationResult>();

        var thread = new Thread(() =>
        {
            Form? form = null;
            try
            {
                form = new Form
                {
                    Text = "Microsoft",
                    Width = 520,
                    Height = 720,
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false
                };
                var web = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(web);

                // Init on the UI thread once the message loop is running, then navigate.
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
                        await web.EnsureCoreWebView2Async(env);
                        web.CoreWebView2.Navigate(uri.ToString());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                        form!.Close();
                    }
                };

                // The redirect to oauth20_desktop.srf carries the code in its query.
                web.SourceChanged += (_, _) =>
                {
                    var src = web.Source;
                    if (src != null && src.ToString().StartsWith(DesktopRedirect, StringComparison.OrdinalIgnoreCase))
                    {
                        tcs.TrySetResult(uriChecker.GetAuthCodeResult(src));
                        form!.Close();
                    }
                };

                // If the window closes without a code, the user cancelled (no-op if already set).
                form.FormClosed += (_, _) =>
                    tcs.TrySetException(new OperationCanceledException("Microsoft sign-in window was closed."));

                using var reg = cancellationToken.Register(() =>
                {
                    try { form!.BeginInvoke(new Action(() => form!.Close())); } catch { }
                });

                _log("[auth] Opened the embedded Microsoft sign-in window.");
                Application.Run(form);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                try { form?.Dispose(); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
    {
        // Used for browser sign-out; just hand it to the system browser.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log("[auth] Could not open sign-out page: " + ex.Message);
        }
        return Task.CompletedTask;
    }
}
