using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiUsageWidget.UI
{
    /// <summary>Development helper used by --screenshot to review the widget visually.</summary>
    internal static class Screenshot
    {
        public static void Save(Window window, string path)
        {
            window.UpdateLayout();

            var root = window.Content as Visual;
            if (root == null) throw new InvalidOperationException("No visual root.");

            double scaleX = 1;
            double scaleY = 1;
            var source = PresentationSource.FromVisual(window);
            if (source != null && source.CompositionTarget != null)
            {
                scaleX = source.CompositionTarget.TransformToDevice.M11;
                scaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            var width = (int)Math.Ceiling(window.ActualWidth * scaleX);
            var height = (int)Math.Ceiling(window.ActualHeight * scaleY);

            var bitmap = new RenderTargetBitmap(
                Math.Max(1, width), Math.Max(1, height),
                96 * scaleX, 96 * scaleY,
                PixelFormats.Pbgra32);

            // Paint the window background first: ClearType text rendered onto a
            // fully transparent surface ends up with alpha 0 and would be
            // invisible in the PNG.
            var background = new DrawingVisual();
            using (var context = background.RenderOpen())
            {
                context.DrawRectangle(
                    window.Background ?? Brushes.White,
                    null,
                    new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            }
            bitmap.Render(background);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            try
            {
                var dump = new System.Text.StringBuilder();
                Dump(root, 0, dump, (UIElement)root);
                File.WriteAllText(path + ".tree.txt", dump.ToString());
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        private static void Dump(DependencyObject node, int depth, System.Text.StringBuilder output, UIElement referenceRoot)
        {
            var text = node as System.Windows.Controls.TextBlock;
            var element = node as FrameworkElement;
            var indent = new string(' ', depth * 2);

            if (text != null)
            {
                output.Append(indent)
                    .Append("TextBlock text=\"").Append(text.Text)
                    .Append("\" fg=").Append(text.Foreground)
                    .Append(" size=").Append(text.FontSize)
                    .Append(" vis=").Append(text.Visibility)
                    .Append(" opacity=").Append(text.Opacity)
                    .Append(" actual=").Append(text.ActualWidth).Append("x").Append(text.ActualHeight)
                    .Append(" offset=").Append(text.TranslatePoint(new Point(0, 0), referenceRoot))
                    .AppendLine();
            }
            else if (element != null)
            {
                output.Append(indent)
                    .Append(element.GetType().Name)
                    .Append(" actual=").Append(element.ActualWidth).Append("x").Append(element.ActualHeight)
                    .Append(" vis=").Append(element.Visibility)
                    .AppendLine();
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Dump(VisualTreeHelper.GetChild(node, i), depth + 1, output, referenceRoot);
            }
        }
    }
}
