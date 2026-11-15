using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    /// <summary>
    /// Read-only statistics view. Kept out of the tray card on purpose: a chart
    /// that grows every card turns a compact widget into a dashboard.
    /// </summary>
    public sealed class StatisticsWindow : Window
    {
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x2F, 0x6F, 0xEB),
            Color.FromRgb(0x12, 0x88, 0x6B),
            Color.FromRgb(0x8B, 0x5C, 0xF6)
        };

        private readonly AppState _state;
        private readonly UsageChart _chart = new UsageChart { Height = 190, Margin = new Thickness(0, 10, 0, 0) };

        private readonly TextBlock _range = new TextBlock();
        private readonly TextBlock _tokensValue = new TextBlock();
        private readonly TextBlock _tokensCaption = new TextBlock();
        private readonly TextBlock _costValue = new TextBlock();
        private readonly TextBlock _costCaption = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly TextBlock _footnote = new TextBlock();
        private readonly StackPanel _legendRows = new StackPanel();
        private readonly StackPanel _segments = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _emptyTitle = new TextBlock();
        private readonly TextBlock _emptyHint = new TextBlock();

        private StackPanel _dataArea;
        private Border _emptyBox;

        private Button _tokensSegment;
        private Button _costSegment;

        private UsageMetric _metric = UsageMetric.Tokens;
        private List<UsageSeries> _allSeries = new List<UsageSeries>();
        private List<UsageSeries> _series = new List<UsageSeries>();
        private readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _backfillRequestedOn = DateTime.MinValue;

        public StatisticsWindow(AppState state, Window owner)
        {
            _state = state;

            Title = "AI Usage - Statistics";
            Width = 660;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            if (owner != null && owner.IsVisible)
            {
                Owner = owner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            Topmost = true;
            Background = Theme.Surface;
            Foreground = Theme.Text;
            FontFamily = Theme.UiFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = Fit(Build());
            _chart.Focusable = true;
            KeyboardNavigation.SetIsTabStop(_chart, true);
            _chart.HoverChanged += OnHover;

            _state.Changed += OnStateChanged;
            Closed += (s, e) => _state.Changed -= OnStateChanged;

            Reload();
        }

        /// <summary>
        /// Caps the dialog to the work area and scrolls the content when the display
        /// is small or scaled, instead of clipping the footer.
        /// </summary>
        private UIElement Fit(UIElement content)
        {
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 60);

            return new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Theme.Surface
            };
        }

        private UIElement Build()
        {
            var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            System.Windows.Documents.TextElement.SetForeground(stack, Theme.Text);

            stack.Children.Add(BuildRangeRow());
            stack.Children.Add(BuildEmptyBox());

            var data = new StackPanel();
            data.Children.Add(BuildHero());
            data.Children.Add(BuildSegments());
            data.Children.Add(_chart);
            AutomationProperties.SetName(_chart, "Daily usage chart");

            _detail.FontFamily = Theme.UiFont;
            _detail.FontSize = 10.5;
            _detail.Foreground = Theme.Muted;
            _detail.Height = 18;
            _detail.Margin = new Thickness(0, 8, 0, 2);
            _detail.Text = "Hover or use the arrow keys on the chart for a daily breakdown.";
            data.Children.Add(_detail);

            data.Children.Add(BuildLegend());
            _dataArea = data;
            stack.Children.Add(data);

            Theme.Spacer(stack, 14);
            stack.Children.Add(Theme.Separator());
            Theme.Spacer(stack, 10);
            stack.Children.Add(BuildFooter());

            return stack;
        }

        private UIElement BuildEmptyBox()
        {
            var text = new StackPanel();
            _emptyTitle.FontFamily = Theme.UiFont;
            _emptyTitle.FontSize = 12;
            _emptyTitle.FontWeight = FontWeights.Medium;
            _emptyTitle.Foreground = Theme.Text;
            text.Children.Add(_emptyTitle);

            _emptyHint.FontFamily = Theme.UiFont;
            _emptyHint.FontSize = 10.5;
            _emptyHint.Foreground = Theme.Muted;
            _emptyHint.TextWrapping = TextWrapping.Wrap;
            _emptyHint.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(_emptyHint);

            _emptyBox = Theme.Card(text);
            _emptyBox.Margin = new Thickness(0, 12, 0, 0);
            _emptyBox.Visibility = Visibility.Collapsed;
            return _emptyBox;
        }

        private UIElement BuildRangeRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _range.FontFamily = Theme.UiFont;
            _range.FontSize = 10.5;
            _range.Foreground = Theme.Muted;
            _range.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(_range);

            var help = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Theme.Track,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Help,
                ToolTip =
                    "Local calendar days. Tokens are input + output only: cached input is excluded " +
                    "because it dominates the raw total.\n\n" +
                    "Cost is an API list-price estimate from models.dev, not a bill. OpenCode Go " +
                    "reports its own figure, which is zero for subscription-backed models."
            };

            var mark = new TextBlock
            {
                Text = "?",
                FontFamily = Theme.UiFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            help.Child = mark;
            AutomationProperties.SetName(help, "How these numbers are calculated");
            Grid.SetColumn(help, 1);
            grid.Children.Add(help);

            return grid;
        }

        private UIElement BuildHero()
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tokens = new StackPanel();
            _tokensValue.FontFamily = Theme.UiFont;
            _tokensValue.FontSize = 30;
            _tokensValue.FontWeight = FontWeights.SemiBold;
            _tokensValue.Foreground = Theme.Text;
            tokens.Children.Add(FixedValue(_tokensValue));

            _tokensCaption.Text = "input + output tokens";
            _tokensCaption.FontFamily = Theme.UiFont;
            _tokensCaption.FontSize = 10.5;
            _tokensCaption.Foreground = Theme.Muted;
            tokens.Children.Add(_tokensCaption);
            grid.Children.Add(tokens);

            var cost = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(24, 0, 0, 0)
            };
            _costValue.FontFamily = Theme.UiFont;
            _costValue.FontSize = 18;
            _costValue.FontWeight = FontWeights.SemiBold;
            _costValue.Foreground = Theme.Muted;
            _costValue.TextAlignment = TextAlignment.Right;
            cost.Children.Add(FixedValue(_costValue));

            _costCaption.Text = "estimated cost";
            _costCaption.FontFamily = Theme.UiFont;
            _costCaption.FontSize = 10.5;
            _costCaption.Foreground = Theme.Muted;
            _costCaption.TextAlignment = TextAlignment.Right;
            cost.Children.Add(_costCaption);

            Grid.SetColumn(cost, 1);
            grid.Children.Add(cost);

            return grid;
        }

        /// <summary>
        /// Reserves one line of hero height so switching metrics does not move the
        /// chart. The number is bottom-aligned, which keeps both metrics on the same
        /// baseline at different sizes.
        /// </summary>
        private static Grid FixedValue(TextBlock value)
        {
            value.VerticalAlignment = VerticalAlignment.Bottom;
            var host = new Grid { Height = 40 };
            host.Children.Add(value);
            return host;
        }

        private UIElement BuildSegments()
        {
            var track = new Border
            {
                Background = Theme.Track,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0)
            };

            _tokensSegment = Segment("Tokens", UsageMetric.Tokens);
            _costSegment = Segment("Cost", UsageMetric.Cost);
            _segments.Children.Add(_tokensSegment);
            _segments.Children.Add(_costSegment);

            track.Child = _segments;
            return track;
        }

        private Button Segment(string text, UsageMetric metric)
        {
            var button = Theme.Button(text, Theme.ButtonKind.Ghost, () => SelectMetric(metric));
            button.MinHeight = 26;
            button.Padding = new Thickness(14, 3, 14, 3);
            button.FontSize = 11.5;
            button.Margin = new Thickness(0);
            return button;
        }

        private void SelectMetric(UsageMetric metric)
        {
            _metric = metric;
            StyleSegments();
            _chart.SetData(_series, _metric);
            UpdateSummary();
            RebuildLegend();
        }

        private void StyleSegments()
        {
            StyleSegment(_tokensSegment, _metric == UsageMetric.Tokens);
            StyleSegment(_costSegment, _metric == UsageMetric.Cost);
        }

        private static void StyleSegment(Button button, bool active)
        {
            button.Background = active ? Theme.CardBg : Brushes.Transparent;
            button.BorderBrush = active ? Theme.Border : Brushes.Transparent;
            button.BorderThickness = new Thickness(1);
            button.Foreground = active ? Theme.Text : Theme.Muted;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private UIElement BuildLegend()
        {
            var host = new Border { Margin = new Thickness(0, 6, 0, 0) };
            var stack = new StackPanel();

            stack.Children.Add(LegendHeaderRow());
            stack.Children.Add(_legendRows);

            host.Child = stack;
            return host;
        }

        private static UIElement LegendHeaderRow()
        {
            var grid = LegendGrid();
            var provider = Theme.Caption("Provider");
            provider.FontSize = 9.5;
            provider.Foreground = Theme.Faint;
            Grid.SetColumn(provider, 1);
            grid.Children.Add(provider);

            var tokens = Theme.Caption("Tokens");
            tokens.FontSize = 9.5;
            tokens.Foreground = Theme.Faint;
            tokens.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(tokens, 2);
            grid.Children.Add(tokens);

            var cost = Theme.Caption("Cost");
            cost.FontSize = 9.5;
            cost.Foreground = Theme.Faint;
            cost.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(cost, 3);
            grid.Children.Add(cost);

            grid.Margin = new Thickness(8, 0, 8, 4);
            return grid;
        }

        private static Grid LegendGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            return grid;
        }

        private void RebuildLegend()
        {
            _legendRows.Children.Clear();

            if (_allSeries.Count == 0)
            {
                _legendRows.Children.Add(Theme.Footnote("No providers to show."));
                return;
            }

            for (int i = 0; i < _allSeries.Count; i++)
            {
                var item = _allSeries[i];
                var hidden = _hidden.Contains(item.Id);

                long total = 0;
                var cost = 0.0;
                for (int d = 0; d < item.Days.Count; d++)
                {
                    var day = item.Days[d];
                    total += day.Billable;
                    if (day.CostUsd.HasValue) cost += day.CostUsd.Value;
                }

                var grid = LegendGrid();
                grid.Margin = new Thickness(8, 0, 8, 0);

                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(item.Color),
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Add(dot);

                var name = Theme.Body(item.Name);
                name.FontSize = 11.5;
                name.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);

                var tokens = new TextBlock
                {
                    Text = ChartMath.FormatCompact(total),
                    FontFamily = Theme.UiFont,
                    FontSize = 11.5,
                    FontWeight = _metric == UsageMetric.Tokens ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = _metric == UsageMetric.Tokens ? Theme.Text : Theme.Muted,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tokens, 2);
                grid.Children.Add(tokens);

                var money = new TextBlock
                {
                    Text = ChartMath.FormatMoney(cost),
                    FontFamily = Theme.UiFont,
                    FontSize = 11.5,
                    FontWeight = _metric == UsageMetric.Cost ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = _metric == UsageMetric.Cost ? Theme.Text : Theme.Muted,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(money, 3);
                grid.Children.Add(money);

                var id = item.Id;
                var row = Theme.RowButton(
                    grid,
                    () => Toggle(id),
                    (hidden ? "Show " : "Hide ") + item.Name);
                row.Padding = new Thickness(0, 3, 0, 3);
                row.Opacity = hidden ? 0.45 : 1.0;
                row.ToolTip = hidden ? "Show " + item.Name : "Hide " + item.Name;
                _legendRows.Children.Add(row);
            }
        }

        private void Toggle(string providerId)
        {
            if (!_hidden.Remove(providerId)) _hidden.Add(providerId);
            ApplyVisibility();
        }

        private void OnStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Reload));
                return;
            }
            Reload();
        }

        private void Reload()
        {
            var snapshots = _state.Snapshots;
            var series = new List<UsageSeries>();
            var incomplete = false;
            string first = null;
            string last = null;

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot.Daily.Count == 0) continue;

                series.Add(new UsageSeries
                {
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    Color = ColorFor(snapshot.Id),
                    Days = snapshot.Daily
                });

                if (snapshot.DailyIncomplete) incomplete = true;
                if (first == null) first = snapshot.Daily[0].Date;
                var tail = snapshot.Daily[snapshot.Daily.Count - 1].Date;
                if (last == null || string.CompareOrdinal(tail, last) > 0) last = tail;
            }

            _allSeries = series;
            StyleSegments();

            if (!_state.Config.ShowConsumption)
            {
                ShowEmpty(
                    "Local token consumption is turned off.",
                    "Turn on \"Show local token consumption\" in Settings to collect usage.");
                return;
            }

            if (series.Count == 0)
            {
                ShowEmpty(
                    "No local usage data yet.",
                    "Codex and Claude are read from their session files. OpenCode Go needs the v2 service running.");
                return;
            }

            _range.Text = "Last 30 days  \u00b7  " + ShortDate(first) + " \u2013 " + ShortDate(last);
            ShowData();
            ApplyVisibility();

            var note = "Cost is an API list-price estimate from models.dev, not a bill. \"+\" means a model had no published price.";
            if (incomplete)
            {
                note = "Collecting the 30-day history for at least one provider. " + note;
                RequestBackfill();
            }

            _footnote.Text = note;
        }

        private void ShowEmpty(string message, string hint)
        {
            _series = new List<UsageSeries>();
            _range.Text = string.Empty;
            _emptyTitle.Text = message;
            _emptyHint.Text = hint;
            _emptyBox.Visibility = Visibility.Visible;
            if (_dataArea != null) _dataArea.Visibility = Visibility.Collapsed;
            _legendRows.Children.Clear();
            _footnote.Text = string.Empty;
            _chart.SetData(_series, _metric);
        }

        private void ShowData()
        {
            _emptyBox.Visibility = Visibility.Collapsed;
            if (_dataArea != null) _dataArea.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Rebuilds the visible series after a legend toggle. Hidden providers keep
        /// their row so they can be brought back.
        /// </summary>
        private void ApplyVisibility()
        {
            var visible = new List<UsageSeries>(_allSeries.Count);
            for (int i = 0; i < _allSeries.Count; i++)
            {
                if (_hidden.Contains(_allSeries[i].Id)) continue;
                visible.Add(_allSeries[i]);
            }

            _series = visible;
            _chart.SetData(_series, _metric);
            UpdateSummary();
            RebuildLegend();
        }

        private static Color ColorFor(string providerId)
        {
            if (providerId == "opencode-go") return Palette[0];
            if (providerId == "codex") return Palette[1];
            if (providerId == "claude") return Palette[2];
            return Palette[0];
        }

        private void UpdateSummary()
        {
            long billable = 0;
            var cost = 0.0;
            var priced = false;
            long unpriced = 0;

            for (int i = 0; i < _series.Count; i++)
            {
                for (int d = 0; d < _series[i].Days.Count; d++)
                {
                    var day = _series[i].Days[d];
                    billable += day.Billable;
                    if (day.CostUsd.HasValue)
                    {
                        cost += day.CostUsd.Value;
                        priced = true;
                    }
                    unpriced += day.UnpricedTokens;
                }
            }

            _tokensValue.Text = ChartMath.FormatCompact(billable);
            _tokensCaption.Text = "input + output tokens";

            _costValue.Text = (priced ? ChartMath.FormatMoney(cost) : "n/a") + (unpriced > 0 ? " +" : string.Empty);
            _costCaption.Text = "estimated cost";

            var tokensPrimary = _metric == UsageMetric.Tokens;
            _tokensValue.FontSize = tokensPrimary ? 30 : 18;
            _tokensValue.Foreground = tokensPrimary ? Theme.Text : Theme.Muted;
            _costValue.FontSize = tokensPrimary ? 18 : 30;
            _costValue.Foreground = tokensPrimary ? Theme.Muted : Theme.Text;
        }

        private void OnHover(int index)
        {
            if (index < 0 || _series.Count == 0)
            {
                _detail.Text = "Hover or use the arrow keys on the chart for a daily breakdown.";
                return;
            }

            DailyUsage first = null;
            for (int i = 0; i < _series.Count; i++)
            {
                if (index < _series[i].Days.Count)
                {
                    first = _series[i].Days[index];
                    break;
                }
            }

            if (first == null)
            {
                _detail.Text = string.Empty;
                return;
            }

            var parts = new List<string> { ShortDate(first.Date) };
            for (int i = 0; i < _series.Count; i++)
            {
                if (index >= _series[i].Days.Count) continue;
                var day = _series[i].Days[index];
                parts.Add(ProviderShortName.Of(_series[i].Id) + " " + ChartMath.FormatCompact(day.Billable));
            }

            var cost = 0.0;
            var priced = false;
            for (int i = 0; i < _series.Count; i++)
            {
                if (index >= _series[i].Days.Count) continue;
                var day = _series[i].Days[index];
                if (!day.CostUsd.HasValue) continue;
                cost += day.CostUsd.Value;
                priced = true;
            }

            if (priced) parts.Add(ChartMath.FormatMoney(cost));

            _detail.Text = string.Join("   \u00b7   ", parts.ToArray());
        }

        private void RequestBackfill()
        {
            // The service already refuses a second backfill on the same day; this
            // guard keeps a still-open window from re-running the cheap refresh
            // every two minutes while a gap cannot be filled.
            var today = DateTime.Now.Date;
            if (_backfillRequestedOn == today) return;
            if (_state.Refreshing) return;

            _backfillRequestedOn = today;
            _state.RequestHistoryBackfill();
        }

        private UIElement BuildFooter()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _footnote.FontFamily = Theme.UiFont;
            _footnote.FontSize = 9.5;
            _footnote.Foreground = Theme.Faint;
            _footnote.TextWrapping = TextWrapping.Wrap;
            _footnote.Margin = new Thickness(0, 0, 16, 0);
            _footnote.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(_footnote);

            var close = Theme.Button("Close", Theme.ButtonKind.Secondary, Close);
            close.IsCancel = true;
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            return grid;
        }

        private static string ShortDate(string date)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                return date;
            }
            return parsed.ToString("d MMM", CultureInfo.InvariantCulture);
        }

        /// <summary>Development helper: renders the window to a PNG for visual review.</summary>
        public void SaveScreenshot(string path)
        {
            Screenshot.Save(this, path);
        }
    }
}




