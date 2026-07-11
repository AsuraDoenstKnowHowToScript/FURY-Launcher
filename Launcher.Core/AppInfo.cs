// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core;

/// <summary>
/// Single source of truth for the application's identity (name, version, data
/// folder). Used by the window title/About, the on-disk data root and the
/// Modrinth User-Agent so a rebrand only touches this file.
/// </summary>
public static class AppInfo
{
    public const string Name = "FURY Launcher";
    public const string Version = "0.3.3-beta";

    /// <summary>Copyright/licença exibida no título/Sobre. Software proprietário.</summary>
    public const string Copyright = "© 2026 Suny. Todos os direitos reservados. Software proprietário — ver LICENSE.";

    /// <summary>Folder name under %APPDATA% where all launcher data lives.</summary>
    public const string DataFolderName = "FURY Launcher";
}
