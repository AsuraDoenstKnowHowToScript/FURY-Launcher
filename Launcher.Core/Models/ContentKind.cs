// FURY Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. Use, copying, modification or distribution without prior
// written permission is prohibited. See the LICENSE file.
// "FURY" is a trademark of the holder. Not affiliated with Mojang/Microsoft.

namespace Launcher.Core.Models;

/// <summary>
/// A type of installable content from Modrinth. Selects the Modrinth
/// <c>project_type</c> facet for search and the destination folder in the
/// instance's <c>.minecraft</c> (mods / shaderpacks / datapacks).
/// </summary>
public enum ContentKind
{
    Mod,
    Shader,
    Datapack
}
