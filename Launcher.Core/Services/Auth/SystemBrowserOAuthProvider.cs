// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using XboxAuthNet.Game;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth.CodeFlow;

namespace Launcher.Core.Services;

/// <summary>
/// OAuth provider for CmlLib's <c>JELoginHandler</c> whose interactive step uses a
/// caller-supplied <see cref="IWebUI"/> (an embedded WebView login window provided by
/// the app, or a paste fallback). We keep the default registered redirect
/// (<c>oauth20_desktop.srf</c>) because the Minecraft MSA client rejects any other
/// redirect. Silent/sign-out/validation are delegated to the stock provider so token
/// caching behaves exactly like the default.
/// </summary>
public sealed class SystemBrowserOAuthProvider : IAuthenticationProvider
{
    private readonly MicrosoftOAuthClientInfo _clientInfo;
    private readonly MicrosoftOAuthCodeFlowProvider _inner;
    private readonly Func<IWebUI> _webUiFactory;

    public SystemBrowserOAuthProvider(MicrosoftOAuthClientInfo clientInfo, Func<IWebUI> webUiFactory)
    {
        _clientInfo = clientInfo;
        _inner = new MicrosoftOAuthCodeFlowProvider(clientInfo);
        _webUiFactory = webUiFactory;
    }

    public IAuthenticator Authenticate() => _inner.Authenticate();
    public IAuthenticator AuthenticateSilently() => _inner.AuthenticateSilently();
    public IAuthenticator ClearSession() => _inner.ClearSession();
    public IAuthenticator Signout() => _inner.Signout();
    public ISessionValidator CreateSessionValidator() => _inner.CreateSessionValidator();

    public IAuthenticator AuthenticateInteractively()
    {
        var webUi = _webUiFactory();
        // Single-arg overload keeps the default desktop redirect, the only one
        // registered for the Minecraft MSA client.
        return new MicrosoftOAuthBuilder(_clientInfo).Interactive(codeFlow => codeFlow.WithWebUI(webUi));
    }
}
