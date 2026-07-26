// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Avalonia.Controls;

namespace Launcher.App.Views;

/// <summary>
/// The Modpacks screen. Pure view: browsing and installing live in
/// <see cref="ViewModels.ModpacksViewModel"/>, bound through the DataContext.
/// </summary>
public partial class ModpacksView : UserControl
{
    public ModpacksView() => InitializeComponent();
}
