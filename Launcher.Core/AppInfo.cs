// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core;

/// <summary>
/// Single source of truth for the application's identity (name, version, data
/// folder). Used by the window title/About, the on-disk data root and the
/// Modrinth User-Agent so a rebrand only touches this file.
/// </summary>
public static class AppInfo
{
    public const string Name = "Bonfire Launcher";
    public const string Version = "1.5.2";

    /// <summary>Copyright/licença exibida no título/Sobre. Software proprietário.</summary>
    public const string Copyright = "© 2026 Suny. Todos os direitos reservados. Software proprietário. Consulte o LICENSE.";

    /// <summary>Folder name under %APPDATA% where all launcher data lives.</summary>
    public const string DataFolderName = "Bonfire Launcher";

    /// <summary>
    /// What the data folder was called before the rename. Everything a user has — instances,
    /// accounts, play time — lives under it, so the new name has to adopt the old folder rather
    /// than start an empty one beside it. See <c>LauncherPaths</c>.
    /// </summary>
    public const string LegacyDataFolderName = "FURY Launcher";

    /// <summary>GitHub repository the auto-updater checks for new releases.</summary>
    public const string RepoOwner = "AsuraDoenstKnowHowToScript";
    public const string RepoName = "FURY-Launcher";
}
