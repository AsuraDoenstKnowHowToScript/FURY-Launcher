// FURY Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. See the LICENSE file. "FURY" is a trademark of the holder.

using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Launcher.App.ViewModels;

namespace Launcher.App;

/// <summary>
/// Resolves a View from a ViewModel by name convention: replace "ViewModels" with
/// "Views" and drop the "ViewModel" suffix. Phase 1 scaffolding for Phase 2 screens.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;

        var name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
