// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System;
using System.Threading.Tasks;

namespace Launcher.App.Services;

/// <summary>
/// The window-level affordances a screen needs but must not own: modal dialogs, the file
/// picker, the toast and the log. Implemented by the main window, so a screen can move into
/// its own view model without dragging the whole window along with it.
/// </summary>
public interface IDialogService
{
    /// <summary>Modal yes/no. True when the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string? confirmLabel = null, string? cancelLabel = null);

    /// <summary>Modal acknowledge-with-"don't show again" warning.</summary>
    Task<(bool proceed, bool dontShowAgain)> WarnAckAsync(string title, string message, string ackButton);

    /// <summary>Native PNG picker; null when cancelled.</summary>
    Task<string?> PickImageAsync(string title);

    /// <summary>Transient toast notification.</summary>
    void Toast(string message, bool error = false);

    /// <summary>Appends a line to the session log panel.</summary>
    void Log(string line);

    /// <summary>Runs an async action, surfacing any error exactly the way the window does.</summary>
    Task RunGuardedAsync(Func<Task> action);

    /// <summary>Puts text on the system clipboard. Needs the window, which owns the TopLevel.</summary>
    Task CopyAsync(string text);
}
