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

    /// <summary>
    /// Default Java executable used when an instance is on "Auto". Null lets the
    /// launcher manage the runtime itself (recommended). Set from the Settings tab
    /// to steer clear of a broken Oracle "javapath" stub.
    /// </summary>
    public string? DefaultJavaPath { get; set; }

    /// <summary>
    /// Id of the currently selected <see cref="Account"/> — the single source of truth
    /// for who launches and whose skin is applied. Null = no account selected yet.
    /// </summary>
    public string? ActiveAccountId { get; set; }

    /// <summary>
    /// Draw the launcher on the GPU. Off falls back to software rendering, which is the escape
    /// hatch for broken drivers where the window flickers or refuses to paint. Read once at
    /// startup, so changing it needs a restart.
    /// </summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>False minimises the launcher once the game is up, instead of leaving it in the way.</summary>
    public bool KeepLauncherOpen { get; set; } = true;

    /// <summary>
    /// JVM arguments pre-filled when creating a new instance. Empty means "let the launcher and
    /// the JVM decide", which is the right default for most people.
    /// </summary>
    public string DefaultJvmArgs { get; set; } = "";
}
