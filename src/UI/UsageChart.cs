using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    public enum UsageMetric
    {
        Tokens,
        Cost
    }

    public sealed class UsageSeries
    {
        public string Id;
        public string Name;
        public Color Color;
        public List<DailyUsage> Days = new List<DailyUsage>();

        public double ValueOf(DailyUsage day, UsageMetric metric)
        {
            if (day == null) return 0;
            if (metric == UsageMetric.Tokens) return day.Billable;
            return day.CostUsd.HasValue ? day.CostUsd.Value : 0;
        }
    }

    /// <summary>
    /// Stacked bars drawn directly instead of through a charting package: the
    /// build has no NuGet restore, and 30 days of stacked bars is a handful of
    /// rectangles plus two axis lines.
    /// </summary>
    public sealed class UsageChart : FrameworkElement
    {
        private const int Days = 30;
        private const double PadLeft = 46;
        private const double PadRight = 10;
        private const double PadTop = 10;
        private const double PadBottom = 20;

        private static readonly Typeface LabelTypeface = new Typeface(
            new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        private readonly Brush _gridBrush = Frozen(0x22, 0x0F, 0x17, 0x2A);
        private readonly Brush _labelBrush = Frozen(0xFF, 0x6B, 0x72, 0x80);
        private readonly Brush _highlightBrush = Frozen(0x14, 0x0F, 0x17, 0x2A);

        private List<UsageSeries> _series = new List<UsageSeries>();
        private List<Brush> _brushes = new List<Brush>();
        private UsageMetric _metric = UsageMetric.Tokens;
        private int _highlight = -1;

        public event Action<int> HoverChanged;

        public UsageMetric Metric
        {
            get { return _metric; }
        }

        public int Highlight
        {
            get { return _highlight; }
        }

        public void SetData(List<UsageSeries> series, UsageMetric metric)
        {
            _series = series ?? new List<UsageSeries>();
            _metric = metric;

            _brushes = new List<Brush>(_series.Count);
            for (int i = 0; i < _series.Count; i++)
            {
                var brush = new SolidColorBrush(_series[i].Color);
                brush.Freeze();
                _brushes.Add(brush);
            }

            InvalidateVisual();
        }

        public void SetHighlight(int index)
        {
            if (_highlight == index) return;
            _highlight = index;
            InvalidateVisual();

            var handler = HoverChanged;
            if (handler != null) handler(index);
        }

        // -------------------------------------------------------------- geometry

        internal static double SlotWidth(double width)
        {
            var plot = Math.Max(1, width - PadLeft - PadRight);
            return plot / Days;
        }

        internal static int IndexAt(double x, double width)
        {
            var slot = SlotWidth(width);
            if (slot <= 0) return -1;

            var index = (int)Math.Floor((x - PadLeft) / slot);
            if (index < 0 || index >= Days) return -1;
            return index;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= PadLeft + PadRight || height <= PadTop + PadBottom) return;

            var plotWidth = width - PadLeft - PadRight;
            var plotHeight = height - PadTop - PadBottom;
            var slot = plotWidth / Days;
            var barWidth = Math.Max(2.0, slot - 2.0);

            var peak = 0.0;
            for (int i = 0; i < Days; i++)
            {
                var total = 0.0;
                for (int s = 0; s < _series.Count; s++)
                {
                    total += _series[s].ValueOf(DayAt(_series[s], i), _metric);
                }
                if (total > peak) peak = total;
            }

            var scale = ChartMath.NiceMax(peak);

            for (int line = 0; line <= 2; line++)
            {
                var value = scale * line / 2.0;
                var y = PadTop + plotHeight - (plotHeight * line / 2.0);
                dc.DrawRectangle(_gridBrush, null, new Rect(PadLeft, Math.Round(y) + 0.5, plotWidth, 1));

                var text = Text(FormatAxis(value), 9);
                dc.DrawText(text, new Point(PadLeft - 6 - text.Width, y - text.Height / 2.0));
            }

            if (peak <= 0)
            {
                var empty = Text("No usage recorded", 10);
                dc.DrawText(empty, new Point(
                    PadLeft + (plotWidth - empty.Width) / 2.0,
                    PadTop + (plotHeight - empty.Height) / 2.0));
                return;
            }

            if (_highlight >= 0 && _highlight < Days)
            {
                dc.DrawRectangle(_highlightBrush, null,
                    new Rect(PadLeft + slot * _highlight, PadTop, slot, plotHeight));
            }

            for (int i = 0; i < Days; i++)
            {
                var x = PadLeft + slot * i + (slot - barWidth) / 2.0;
                var bottom = PadTop + plotHeight;

                for (int s = 0; s < _series.Count; s++)
                {
                    var value = _series[s].ValueOf(DayAt(_series[s], i), _metric);
                    if (value <= 0) continue;

                    var barHeight = plotHeight * (value / scale);
                    if (barHeight < 0.5) barHeight = 0.5;
                    bottom -= barHeight;

                    dc.DrawRectangle(_brushes[s], null, new Rect(x, bottom, barWidth, barHeight));
                }
            }

            DrawDateLabel(dc, 0, PadLeft + slot / 2.0, plotWidth);
            DrawDateLabel(dc, 7, PadLeft + slot * 7 + slot / 2.0, plotWidth);
            DrawDateLabel(dc, 14, PadLeft + slot * 14 + slot / 2.0, plotWidth);
            DrawDateLabel(dc, 21, PadLeft + slot * 21 + slot / 2.0, plotWidth);

            var last = FirstDayAt(Days - 1);
            if (last != null)
            {
                var text = Text(ShortDate(last.Date), 9);
                dc.DrawText(text, new Point(
                    PadLeft + plotWidth - text.Width,
                    PadTop + plotHeight + 5));
            }
        }

        private void DrawDateLabel(DrawingContext dc, int index, double centerX, double plotWidth)
        {
            var day = FirstDayAt(index);
            if (day == null) return;

            var label = ShortDate(day.Date);
            if (label == null) return;

            var text = Text(label, 9);
            var x = centerX - text.Width / 2.0;
            dc.DrawText(text, new Point(Math.Max(2, Math.Min(x, PadLeft + plotWidth - 40)), PadTop + ActualHeight - PadBottom + 5));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHighlight(IndexAt(e.GetPosition(this).X, ActualWidth));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            SetHighlight(-1);
        }

        private string FormatAxis(double value)
        {
            return _metric == UsageMetric.Cost
                ? ChartMath.FormatMoney(value)
                : ChartMath.FormatCompact(value);
        }

        private static DailyUsage DayAt(UsageSeries series, int index)
        {
            if (series == null || series.Days == null) return null;
            return index >= 0 && index < series.Days.Count ? series.Days[index] : null;
        }

        private DailyUsage FirstDayAt(int index)
        {
            for (int s = 0; s < _series.Count; s++)
            {
                var day = DayAt(_series[s], index);
                if (day != null) return day;
            }
            return null;
        }

        private static string ShortDate(string date)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                return null;
            }
            return parsed.ToString("d MMM", CultureInfo.InvariantCulture);
        }

        private FormattedText Text(string value, double size)
        {
            // The reference assemblies expose the .NET 4.0 FormattedText surface, so
            // the pixelsPerDip overload is not available at compile time. Glyph
            // layout stays in device-independent units either way.
            return new FormattedText(
                value,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                size,
                _labelBrush);
        }

        private static Brush Frozen(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Pure layout helpers so the chart maths can be tested offline.</summary>
    internal static class ChartMath
    {
        public static double NiceMax(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 1;

            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            var normalized = value / magnitude;

            double step;
            if (normalized <= 1) step = 1;
            else if (normalized <= 2) step = 2;
            else if (normalized <= 5) step = 5;
            else step = 10;

            return step * magnitude;
        }

        public static string FormatCompact(double value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000) return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        public static string FormatMoney(double value)
        {
            if (value >= 100) return "$" + value.ToString("0", CultureInfo.InvariantCulture);
            if (value >= 1) return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
            return "$" + value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
