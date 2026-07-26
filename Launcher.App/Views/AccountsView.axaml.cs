// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using Avalonia.Controls;

namespace Launcher.App.Views;

/// <summary>
/// The Accounts screen. Pure view: every behaviour lives in
/// <see cref="ViewModels.AccountsViewModel"/>, bound through the DataContext.
/// </summary>
public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();
}
