// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Localization;

/// <summary>Languages the launcher UI can be shown in. English is the default.</summary>
public enum Language
{
    English,
    Portuguese,
    Dutch,
    ChineseTraditional,
    Russian
}

/// <summary>Display names and short codes for each <see cref="Language"/>.</summary>
public static class LanguageInfo
{
    /// <summary>Order shown in the language dropdown (English first = default).</summary>
    public static readonly Language[] All =
    {
        Language.English,
        Language.Portuguese,
        Language.Dutch,
        Language.ChineseTraditional,
        Language.Russian
    };

    /// <summary>Endonym (the language's own name), for the dropdown.</summary>
    public static string NativeName(Language l) => l switch
    {
        Language.English => "English",
        Language.Portuguese => "Português",
        Language.Dutch => "Nederlands",
        Language.ChineseTraditional => "繁體中文",
        Language.Russian => "Русский",
        _ => l.ToString()
    };

    /// <summary>Short code persisted in settings.json.</summary>
    public static string Code(Language l) => l switch
    {
        Language.English => "en",
        Language.Portuguese => "pt",
        Language.Dutch => "nl",
        Language.ChineseTraditional => "zh-Hant",
        Language.Russian => "ru",
        _ => "en"
    };

    /// <summary>Parses a stored code back to a <see cref="Language"/> (defaults to English).</summary>
    public static Language FromCode(string? code) => code switch
    {
        "pt" => Language.Portuguese,
        "nl" => Language.Dutch,
        "zh-Hant" => Language.ChineseTraditional,
        "ru" => Language.Russian,
        _ => Language.English
    };
}
