// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Avalonia.Controls;

namespace Launcher.App.Views;

/// <summary>
/// The dashboard screen. Pure view: everything it shows comes from
/// <see cref="ViewModels.DashboardViewModel"/> through bindings.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}
