using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace AiUsageWidget.UI
{
    /// <summary>
    /// Placement check against real monitor working areas. The virtual desktop
    /// bounding rectangle is not enough: a saved point can sit in the gap between
    /// monitors or on a monitor that has since been disconnected.
    /// </summary>
    internal static class DesktopPlacement
    {
        private const double MinVisibleWidth = 80;
        private const double MinVisibleHeight = 20;

        public static bool IsVisible(double left, double top, double width, double height)
        {
            var scale = PrimaryDipScale();
            var rect = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));

            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var area = screens[i].WorkingArea;
                var work = new Rect(
                    area.X * scale,
                    area.Y * scale,
                    area.Width * scale,
                    area.Height * scale);

                var intersection = Rect.Intersect(rect, work);
                if (!intersection.IsEmpty &&
                    intersection.Width >= MinVisibleWidth &&
                    intersection.Height >= MinVisibleHeight)
                {
                    return true;
                }
            }

            return false;
        }

        private static double PrimaryDipScale()
        {
            try
            {
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    var dpi = graphics.DpiX;
                    if (dpi > 0) return 96.0 / dpi;
                }
            }
            catch (Exception)
            {
                // Fall through to 100%.
            }

            return 1.0;
        }
    }
}
