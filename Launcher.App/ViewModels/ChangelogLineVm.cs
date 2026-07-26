// Bonfire Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "Bonfire" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.App.ViewModels;

/// <summary>
/// One block of a release note. Release bodies are markdown hard-wrapped at ~72 columns, so
/// rendering them raw reads as shredded text; the changelog parses them into these blocks
/// (heading / bullet / paragraph) and the view styles each kind on its own.
/// </summary>
public sealed class ChangelogLineVm
{
    public ChangelogLineVm(string text, bool isHeading, bool isBullet)
    {
        Text = text;
        IsHeading = isHeading;
        IsBullet = isBullet;
        IsParagraph = !isHeading && !isBullet;
    }

    public string Text { get; }
    public bool IsHeading { get; }
    public bool IsBullet { get; }
    public bool IsParagraph { get; }
}
