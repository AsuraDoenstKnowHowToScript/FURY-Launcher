// Bonfire Launcher
// Copyright © 2026 Suny. All rights reserved.
// Proprietary software. Use, copying, modification or distribution without prior
// written permission is prohibited. See the LICENSE file.
// "Bonfire" is a trademark of the holder. Not affiliated with Mojang/Microsoft.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Launcher.App.Behaviors;

/// <summary>
/// Attached behavior that turns a control's mouse-wheel scrolling into a smooth, interpolated
/// animation instead of the default per-notch jump. Set <c>SmoothScroll.Enabled="True"</c> on a
/// <see cref="ScrollViewer"/> or on a control that hosts one.
///
/// Three things matter for this to actually look smooth:
/// <list type="number">
/// <item>The animated position is kept here as a <see cref="double"/> and never read back from
/// the <see cref="ScrollViewer"/>. Offset is coerced to whole device pixels, so reading it back
/// would quantise every step; once the remaining distance drops below a pixel the motion would
/// stall and then jump, which reads as stuttering right at the end of every scroll.</item>
/// <item>Easing is a function of elapsed time, not of frames, so a 144 Hz display and a 60 Hz
/// display travel the same distance in the same wall-clock time and an uneven frame does not
/// produce an uneven step.</item>
/// <item>The scroll viewer is resolved from the element under the pointer, so a list nested in a
/// page scrolls itself, and hands over to the page once it reaches its own end.</item>
/// </list>
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels moved per wheel notch (delta of 1).</summary>
    private const double StepPixels = 100.0;

    /// <summary>Fraction of the remaining distance covered per 60 Hz frame (higher = snappier).</summary>
    private const double EasePerFrame = 0.22;

    /// <summary>Reference frame time the ease fraction is expressed against.</summary>
    private const double ReferenceFrameMs = 1000.0 / 60.0;

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

        private ScrollViewer? _sv;      // the viewer currently being animated
        private double _current;        // our authoritative position; the viewer's own is quantised
        private double _target;
        private bool _animating;
        private TimeSpan _lastFrame;

        public Controller(Control owner)
        {
            _owner = owner;

            // Tunnel: we have to pre-empt the ScrollViewer's own wheel handler, which would jump.
            owner.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            owner.DetachedFromVisualTree += (_, _) => Stop();
        }

        private void Stop()
        {
            _animating = false;
            _sv = null;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            var sv = Resolve(e.Source as Visual, e.Delta.Y);
            if (sv == null) return; // nothing here can scroll: let it bubble to an outer page

            // Switching viewers (or starting fresh) resyncs from the real offset once.
            if (!ReferenceEquals(sv, _sv) || !_animating)
            {
                _sv = sv;
                _current = sv.Offset.Y;
                _target = _current;
                EnableSubPixel(sv);
            }

            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            _target = Math.Clamp(_target - e.Delta.Y * StepPixels, 0, max);

            e.Handled = true;
            if (!_animating)
            {
                _animating = true;
                _lastFrame = TimeSpan.Zero;
                RequestFrame();
            }
        }

        /// <summary>
        /// Finds the scroll viewer the wheel should drive: the innermost one under the pointer that
        /// still has room in this direction, so a list scrolls itself until it bottoms out and the
        /// page takes over from there.
        /// </summary>
        private ScrollViewer? Resolve(Visual? source, double deltaY)
        {
            if (source != null)
            {
                foreach (var sv in source.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
                    if (HasRoom(sv, deltaY))
                        return sv;
            }

            // The pointer was not over a scrollable viewer (empty area, say): fall back to the one
            // this behavior was attached to.
            var own = _owner as ScrollViewer ?? _owner.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            return own != null && HasRoom(own, deltaY) ? own : null;
        }

        private bool HasRoom(ScrollViewer sv, double deltaY)
        {
            var max = sv.Extent.Height - sv.Viewport.Height;
            if (max <= 0.5) return false;

            // Mid-animation the viewer's offset lags behind where we are heading, so judge by the
            // target instead or a fast flick would bleed into the parent.
            var y = _animating && ReferenceEquals(sv, _sv) ? _target : sv.Offset.Y;
            return deltaY > 0 ? y > 0.5 : y < max - 0.5;
        }

        /// <summary>
        /// The last piece of actually-smooth scrolling, and the one that is easy to miss: layout
        /// rounding. With it on (the default) every arrange is snapped to a whole device pixel, so
        /// a fractional offset never renders where it was asked to — the eased motion lands on
        /// 5px, 4px, 3px, 2px, 1px, 1px... and the unequal steps read as stuttering no matter how
        /// clean the animation is. Turning it off for the scrolled subtree lets the content sit at
        /// fractional positions while it moves. The animation finishes on an exact whole number,
        /// so text is back on the pixel grid whenever the view is at rest.
        /// </summary>
        private static void EnableSubPixel(ScrollViewer sv)
        {
            if (!sv.UseLayoutRounding) return;
            sv.UseLayoutRounding = false; // inherited, so the scrolled content follows
        }

        private void RequestFrame()
        {
            var top = TopLevel.GetTopLevel(_owner);
            if (top == null) { _animating = false; return; }
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            if (!_animating) return;
            var sv = _sv;
            if (sv == null) { _animating = false; return; }

            // Time-based easing: the same fraction per unit of time whatever the refresh rate is.
            var dtMs = _lastFrame == TimeSpan.Zero ? ReferenceFrameMs : (now - _lastFrame).TotalMilliseconds;
            _lastFrame = now;
            dtMs = Math.Clamp(dtMs, 1.0, 64.0); // a hitch must not teleport the view
            var factor = 1.0 - Math.Pow(1.0 - EasePerFrame, dtMs / ReferenceFrameMs);

            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            _target = Math.Clamp(_target, 0, max);

            _current += (_target - _current) * factor;
            if (Math.Abs(_target - _current) < 0.25)
            {
                _current = _target;
                _animating = false;
            }

            sv.Offset = new Vector(sv.Offset.X, _current);
            if (_animating) RequestFrame();
        }
    }
}
