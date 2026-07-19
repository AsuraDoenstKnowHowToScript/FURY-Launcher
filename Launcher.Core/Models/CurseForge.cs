// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.Json.Serialization;

namespace Launcher.Core.Models;

// Minimal DTOs for the CurseForge API (v1). Results are adapted into the shared
// Modrinth* records so the browser UI, version chooser and installer stay identical.

public sealed record CfSearchResponse(
    [property: JsonPropertyName("data")] List<CfMod>? Data);

public sealed record CfModResponse(
    [property: JsonPropertyName("data")] CfMod? Data);

public sealed record CfFilesResponse(
    [property: JsonPropertyName("data")] List<CfFile>? Data);

public sealed record CfMod(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("downloadCount")] long DownloadCount,
    [property: JsonPropertyName("logo")] CfLogo? Logo,
    [property: JsonPropertyName("authors")] List<CfAuthor>? Authors);

public sealed record CfLogo(
    [property: JsonPropertyName("url")] string? Url);

public sealed record CfAuthor(
    [property: JsonPropertyName("name")] string? Name);

public sealed record CfFile(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("modId")] long ModId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
    [property: JsonPropertyName("dependencies")] List<CfDependency>? Dependencies);

public sealed record CfDependency(
    [property: JsonPropertyName("modId")] long ModId,
    // 1=Embedded 2=Optional 3=Required 4=Tool 5=Incompatible 6=Include
    [property: JsonPropertyName("relationType")] int RelationType);
