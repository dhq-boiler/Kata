using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Nodify;
using Nodify.Interactivity;

namespace Kata.App.Graph;

// Trackpad-friendly gesture layer for the class-diagram viewport:
//
//   • Two-finger vertical swipe   → pan vertically   (WM_MOUSEWHEEL, no modifier)
//   • Two-finger horizontal swipe → pan horizontally (WM_MOUSEHWHEEL, hooked below)
//   • Two-finger pinch            → zoom             (Windows Precision Touchpad
//                                                    routes pinch as Ctrl+Wheel
//                                                    to legacy WPF wheel input)
//   • Shift + wheel               → pan horizontally (mouse-user fallback)
//   • Ctrl  + wheel               → zoom             (mouse-user zoom shortcut)
//
// We intercept PreviewMouseWheel ourselves for the unmodified and Shift cases so
// pan speed is tunable (Nodify's built-in PanWithMouseWheel is fixed and feels
// too fast on precision touchpads). Ctrl+wheel falls through to Nodify so it
// still zooms via the configured ZoomModifierKey. WM_MOUSEHWHEEL is dropped
// by WPF's input pipeline entirely, so horizontal two-finger swipes need a
// WndProc hook.
public static class TrackpadPanZoom
{
    // Pan step (graph-space units) per wheel notch (120 wheel delta).
    // Divided by ViewportZoom so screen-space pan speed stays constant.
    private const double VerticalPanUnitsPerNotch = 30.0;
    private const double HorizontalPanUnitsPerNotch = 60.0;

    public static void ConfigureGestures()
    {
        // Leave PanWithMouseWheel = true as a safety net + so ZoomModifierKey is
        // honored for the Ctrl+wheel case that falls through to Nodify.
        var g = EditorGestures.Mappings.Editor;
        g.PanWithMouseWheel = true;
        g.PanVerticalModifierKey = ModifierKeys.None;
        g.PanHorizontalModifierKey = ModifierKeys.Shift;
        g.ZoomModifierKey = ModifierKeys.Control;
    }

    public static void EnableWheelPan(NodifyEditor editor)
    {
        editor.PreviewMouseWheel += (_, e) =>
        {
            var mods = Keyboard.Modifiers;
            double zoom = Math.Max(0.01, editor.ViewportZoom);
            double notches = e.Delta / 120.0;
            var loc = editor.ViewportLocation;

            if (mods == ModifierKeys.None)
            {
                // Positive delta = wheel-up / swipe-up → viewport moves up (Y decreases).
                double dy = -notches * VerticalPanUnitsPerNotch / zoom;
                editor.ViewportLocation = new Point(loc.X, loc.Y + dy);
                e.Handled = true;
            }
            else if (mods == ModifierKeys.Shift)
            {
                double dx = -notches * HorizontalPanUnitsPerNotch / zoom;
                editor.ViewportLocation = new Point(loc.X + dx, loc.Y);
                e.Handled = true;
            }
            // Ctrl+wheel: let Nodify zoom (ZoomModifierKey = Ctrl).
        };
    }

    private const int WM_MOUSEHWHEEL = 0x020E;

    public static void EnableHorizontalWheelPan(Window window, NodifyEditor editor)
    {
        void Attach()
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;

                var local = Mouse.GetPosition(editor);
                if (local.X < 0 || local.Y < 0 ||
                    local.X > editor.ActualWidth || local.Y > editor.ActualHeight)
                {
                    return IntPtr.Zero;
                }

                // WM_MOUSEHWHEEL delta: HIWORD(wParam) signed, +right / -left.
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                double zoom = Math.Max(0.01, editor.ViewportZoom);
                double dx = delta / 120.0 * HorizontalPanUnitsPerNotch / zoom;

                var loc = editor.ViewportLocation;
                editor.ViewportLocation = new Point(loc.X + dx, loc.Y);
                handled = true;
                return IntPtr.Zero;
            });
        }

        if (PresentationSource.FromVisual(window) is not null)
        {
            Attach();
        }
        else
        {
            window.SourceInitialized += (_, _) => Attach();
        }
    }
}
