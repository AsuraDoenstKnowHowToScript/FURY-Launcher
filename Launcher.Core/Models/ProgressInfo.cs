// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// UI-friendly progress DTOs. The Core translates CmlLib's own progress types
/// into these so the UI never references CmlLib directly.
/// </summary>
public sealed record FileProgressInfo(string Name, string EventType, int Total, int Progressed);

public sealed record ByteProgressInfo(long Total, long Progressed, double Ratio);
