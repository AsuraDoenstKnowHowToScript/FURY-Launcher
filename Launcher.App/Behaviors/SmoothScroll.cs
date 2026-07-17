// FURY Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. Use, copying, modification or distribution without prior
// written permission is prohibited. See the LICENSE file.
// "FURY" is a trademark of the holder. Not affiliated with Mojang/Microsoft.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Launcher.App.Behaviors;

/// <summary>
/// Attached behavior that turns a control's mouse-wheel scrolling into a smooth,
/// interpolated animation instead of the default per-notch jump. Set
/// <c>SmoothScroll.Enabled="True"</c> on a <see cref="ScrollViewer"/> or on a control
/// that hosts one (e.g. a virtualized <see cref="ListBox"/>); the behavior finds the
/// inner <see cref="ScrollViewer"/>, accumulates a target offset on each wheel tick,
/// and eases <c>Offset.Y</c> toward it on a ~16 ms timer, clamped to the scrollable
/// range. Virtualization is preserved because it only drives the existing offset.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels moved per wheel notch (delta of 1).</summary>
    private const double StepPixels = 100.0;

    /// <summary>Fraction of the remaining distance covered each tick (higher = snappier).</summary>
    private const double Ease = 0.18;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(SmoothScroll));

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    private static readonly ConditionalWeakTable<Control, Controller> Controllers = new();

    static SmoothScroll()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, e) => OnEnabledChanged(control, e));
    }

    private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>() && !Controllers.TryGetValue(control, out _))
            Controllers.Add(control, new Controller(control));
    }

    private sealed class Controller
    {
        private readonly Control _owner;
        private readonly DispatcherTimer _timer;
        private ScrollViewer? _scrollViewer;
        private double _target;

        public Controller(Control owner)
        {
            _owner = owner;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;

            // Tunnel so we pre-empt the default wheel scroll before it jumps.
            owner.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            owner.AttachedToVisualTree += (_, _) => ResolveScrollViewer();
            owner.DetachedFromVisualTree += (_, _) => _timer.Stop();
            ResolveScrollViewer();
        }

        private void ResolveScrollViewer()
            => _scrollViewer = _owner as ScrollViewer
                               ?? _owner.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            var sv = _scrollViewer ??= _owner.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv == null) return;

            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (max <= 0) return; // nothing to scroll: let the event bubble

            // Resync to the real offset whenever we start fresh, so it never drifts.
            if (!_timer.IsEnabled) _target = sv.Offset.Y;
            _target = Math.Clamp(_target - e.Delta.Y * StepPixels, 0, max);

            e.Handled = true;
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var sv = _scrollViewer;
            if (sv == null) { _timer.Stop(); return; }

            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            _target = Math.Clamp(_target, 0, max);

            var current = sv.Offset.Y;
            var next = current + (_target - current) * Ease;
            if (Math.Abs(_target - next) < 0.5)
            {
                next = _target;
                _timer.Stop();
            }

            sv.Offset = new Vector(sv.Offset.X, next);
        }
    }
}
