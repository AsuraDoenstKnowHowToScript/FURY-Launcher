// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Net;
using System.Net.Sockets;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth.CodeFlow.Parameters;

namespace Launcher.Core.Services;

/// <summary>
/// OAuth provider for CmlLib's <c>JELoginHandler</c> that performs the interactive
/// step through the system browser + a loopback redirect (see
/// <see cref="LoopbackBrowserWebUi"/>). Silent/sign-out/validation are delegated to
/// the stock <see cref="MicrosoftOAuthCodeFlowProvider"/>, so token caching behaves
/// exactly like the default. Only the interactive path is customised.
/// </summary>
public sealed class SystemBrowserOAuthProvider : IAuthenticationProvider
{
    private readonly MicrosoftOAuthClientInfo _clientInfo;
    private readonly MicrosoftOAuthCodeFlowProvider _inner;
    private readonly Action<string> _log;

    public SystemBrowserOAuthProvider(MicrosoftOAuthClientInfo clientInfo, Action<string> log)
    {
        _clientInfo = clientInfo;
        _inner = new MicrosoftOAuthCodeFlowProvider(clientInfo);
        _log = log;
    }

    public IAuthenticator Authenticate() => _inner.Authenticate();
    public IAuthenticator AuthenticateSilently() => _inner.AuthenticateSilently();
    public IAuthenticator ClearSession() => _inner.ClearSession();
    public IAuthenticator Signout() => _inner.Signout();
    public ISessionValidator CreateSessionValidator() => _inner.CreateSessionValidator();

    public IAuthenticator AuthenticateInteractively()
    {
        var redirectUri = $"http://127.0.0.1:{GetFreeLoopbackPort()}/";
        var webUi = new LoopbackBrowserWebUi(redirectUri, _log);

        // The SAME redirect URI is used for the authorize request and the token
        // exchange, so login.live.com accepts it and CmlLib redeems the code cleanly.
        var authParameters = new CodeFlowAuthorizationParameter
        {
            RedirectUri = redirectUri,
            Prompt = "select_account"
        };

        return new MicrosoftOAuthBuilder(_clientInfo)
            .Interactive(codeFlow => codeFlow.WithWebUI(webUi), authParameters);
    }

    /// <summary>Grabs a free TCP port on the loopback interface for the redirect.</summary>
    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}
