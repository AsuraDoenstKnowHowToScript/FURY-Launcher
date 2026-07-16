// FURY Launcher
// Copyright © 2026 Suny. Todos os direitos reservados.
// Software proprietário. Proibido usar, copiar, modificar ou distribuir sem
// autorização por escrito. Consulte o arquivo LICENSE.
// "FURY" é marca do Titular. Projeto não afiliado à Mojang/Microsoft.

using System.Text.Json.Serialization;

namespace Launcher.Core.Models;

/// <summary>A search result from the Modrinth API.</summary>
public sealed record ModrinthHit(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("icon_url")] string? IconUrl = null,
    [property: JsonPropertyName("author")] string? Author = null);

/// <summary>A concrete downloadable version of a Modrinth project.</summary>
public sealed record ModrinthVersion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version_number")] string VersionNumber,
    [property: JsonPropertyName("files")] List<ModrinthFile> Files,
    [property: JsonPropertyName("dependencies")] List<ModrinthDependency>? Dependencies = null)
{
    /// <summary>"1.2.3 · release name" for a version dropdown.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) || Name == VersionNumber
        ? VersionNumber
        : $"{VersionNumber} · {Name}";
}

public sealed record ModrinthFile(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("primary")] bool Primary);

/// <summary>A dependency another Modrinth version declares (required/optional/etc.).</summary>
public sealed record ModrinthDependency(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("dependency_type")] string? DependencyType);
