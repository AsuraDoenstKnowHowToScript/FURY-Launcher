// FURY Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. See the LICENSE file. "FURY" is a trademark of the holder.

using System;
using Launcher.Core.Models;

namespace Launcher.App.Services;

/// <summary>
/// App-wide "currently selected instance". Home owns the selection; Mods and other
/// screens read it, so there is a single instance picker instead of one per screen.
/// Registered as a singleton in the DI container.
/// </summary>
public sealed class SelectedInstanceService
{
    private Instance? _current;

    public Instance? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) return;
            _current = value;
            Changed?.Invoke(this, value);
        }
    }

    /// <summary>Raised whenever the selected instance changes (may be null).</summary>
    public event EventHandler<Instance?>? Changed;
}
