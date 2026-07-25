// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Avalonia.Controls;

namespace Launcher.App.Views;

/// <summary>
/// The Servers screen. Pure view: the pinging and the lists live in
/// <see cref="ViewModels.ServersViewModel"/>, bound through the DataContext.
/// </summary>
public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();
}
