// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>Small persisted UI preferences (things the user can dismiss).</summary>
public sealed class LauncherSettings
{
    /// <summary>User ticked "não mostrar novamente" on the offline nick-change warning.</summary>
    public bool SuppressNickChangeWarning { get; set; }

    /// <summary>UI language code (BCP-47-ish): en, pt, nl, zh-Hant, ru. Default English.</summary>
    public string Language { get; set; } = "en";
}
