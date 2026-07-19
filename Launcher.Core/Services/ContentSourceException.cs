// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Services;

/// <summary>Why a content-source (Modrinth / CurseForge) request failed, so the UI can
/// tell "the key was rejected" apart from "no network" and "genuinely nothing found".</summary>
public enum ContentSourceErrorKind
{
    Auth,     // 401/403, or a locally-invalid key (wrong length) — do not blame the query
    Network,  // no connection / DNS / TLS — the request never reached the server
    Server    // any other non-success status from the API
}

/// <summary>A typed failure from a content source. Never carries the API key.</summary>
public sealed class ContentSourceException : Exception
{
    public ContentSourceErrorKind Kind { get; }
    public string Source { get; }
    public int? StatusCode { get; }

    public ContentSourceException(ContentSourceErrorKind kind, string source, int? statusCode = null, Exception? inner = null)
        : base($"{source} request failed ({kind}{(statusCode is { } c ? $", HTTP {c}" : "")}).", inner)
    {
        Kind = kind;
        Source = source;
        StatusCode = statusCode;
    }
}
