using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        private readonly UsageChart _chart = new UsageChart { Height = 170, Margin = new Thickness(0, 6, 0, 2) };
        private readonly TextBlock _range = new TextBlock();
        private readonly TextBlock _summary = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly TextBlock _footnote = new TextBlock();
        private readonly StackPanel _legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Button _tokensButton;
        private readonly Button _costButton;

        private UsageMetric _metric = UsageMetric.Tokens;
        private List<UsageSeries> _allSeries = new List<UsageSeries>();
        private List<UsageSeries> _series = new List<UsageSeries>();
        private readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _backfillRequestedOn = DateTime.MinValue;

        public StatisticsWindow(AppState state, Window owner)
        {
            _state = state;

            Title = "AI Usage - Statistics";
            Width = 620;
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
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
            Foreground = Brushes.Black;
            FontFamily = new FontFamily("Segoe UI");

            _tokensButton = MetricButton("Tokens", UsageMetric.Tokens);
            _costButton = MetricButton("Cost", UsageMetric.Cost);

            Content = Build();
            _chart.HoverChanged += OnHover;

            _state.Changed += OnStateChanged;
            Closed += (s, e) => _state.Changed -= OnStateChanged;

            Reload();
        }

        private UIElement Build()
        {
            var stack = new StackPanel { Margin = new Thickness(16) };
            System.Windows.Documents.TextElement.SetForeground(stack, Brushes.Black);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            head.Children.Add(new TextBlock
            {
                Text = "Usage statistics",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center
            });

            var close = new Button
            {
                Content = "Close",
                Padding = new Thickness(10, 3, 10, 3),
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = "Local calendar days. Tokens are input + output only: cached input is excluded because it dominates the raw total.",
                FontSize = 10,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            _range.FontSize = 10;
            _range.Foreground = Brushes.Gray;
            _range.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(_range);

            _summary.FontSize = 12;
            _summary.Foreground = Brushes.Black;
            _summary.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(_summary);

            var modes = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };
            modes.Children.Add(_tokensButton);
            modes.Children.Add(_costButton);
            stack.Children.Add(modes);

            stack.Children.Add(_chart);
            AutomationProperties.SetName(_chart, "Daily usage chart");

            _detail.FontSize = 10;
            _detail.Foreground = Brushes.Gray;
            _detail.Height = 15;
            _detail.Text = "Hover a bar for the daily breakdown.";
            stack.Children.Add(_detail);

            stack.Children.Add(_legend);

            _footnote.FontSize = 9.5;
            _footnote.Foreground = Brushes.Gray;
            _footnote.TextWrapping = TextWrapping.Wrap;
            _footnote.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(_footnote);

            return stack;
        }

        private Button MetricButton(string text, UsageMetric metric)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (s, e) =>
            {
                _metric = metric;
                StyleMetricButtons();
                _chart.SetData(_series, _metric);
                UpdateSummary();
                BuildLegend();
            };
            return button;
        }

        private void StyleMetricButtons()
        {
            var active = new SolidColorBrush(Color.FromRgb(0xE3, 0xEA, 0xF9));
            var inactive = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

            _tokensButton.Background = _metric == UsageMetric.Tokens ? active : inactive;
            _costButton.Background = _metric == UsageMetric.Cost ? active : inactive;
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

                var item = new UsageSeries
                {
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    Color = ColorFor(snapshot.Id),
                    Days = snapshot.Daily
                };
                series.Add(item);
                if (snapshot.DailyIncomplete) incomplete = true;

                if (first == null) first = snapshot.Daily[0].Date;
                var tail = snapshot.Daily[snapshot.Daily.Count - 1].Date;
                if (last == null || string.CompareOrdinal(tail, last) > 0) last = tail;
            }

            _allSeries = series;

            if (!_state.Config.ShowConsumption)
            {
                _series = series;
                _chart.SetData(_series, _metric);
                _range.Text = string.Empty;
                _summary.Text = "Local token consumption is turned off in Settings.";
                _legend.Children.Clear();
                _footnote.Text = "Turn on \"Show local token consumption\" to collect usage.";
                return;
            }

            if (series.Count == 0)
            {
                _series = series;
                _chart.SetData(_series, _metric);
                _range.Text = "No local usage data yet.";
                _summary.Text = string.Empty;
                _legend.Children.Clear();
                _footnote.Text =
                    "Codex and Claude are read from their local session files. OpenCode Go needs the v2 service running.";
                return;
            }

            _range.Text = first == null
                ? string.Empty
                : "Last 30 local days (" + ShortDate(first) + " - " + ShortDate(last) + ")";

            ApplyVisibility();
            StyleMetricButtons();

            var note = "Cost is an API list-price estimate from models.dev (cached), not a bill; " +
                       "\"+\" means some models have no published price. " +
                       "OpenCode Go reports its own cost, which is zero for subscription-backed models.";
            if (incomplete)
            {
                note = "Collecting the 30-day history for at least one provider; empty days may still be unknown. " + note;
                RequestBackfill();
            }

            _footnote.Text = note;
        }

        /// <summary>
        /// Rebuilds the visible series after a legend toggle. Hidden providers keep
        /// their slot in the legend so they can be brought back.
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
            BuildLegend();
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

            var costText = priced ? ChartMath.FormatMoney(cost) : "n/a";
            if (unpriced > 0) costText += " +";

            _summary.Text = _metric == UsageMetric.Tokens
                ? ChartMath.FormatCompact(billable) + " tokens in 30 days"
                : costText + " in 30 days";
        }

        private void BuildLegend()
        {
            _legend.Children.Clear();

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

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0),
                    Opacity = hidden ? 0.35 : 1.0,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = hidden ? "Show " + item.Name : "Hide " + item.Name
                };

                row.Children.Add(new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(item.Color),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });

                var text = _metric == UsageMetric.Tokens
                    ? item.Name + " " + ChartMath.FormatCompact(total)
                    : item.Name + " " + ChartMath.FormatMoney(cost);
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 9.5,
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var id = item.Id;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    Toggle(id);
                };

                _legend.Children.Add(row);
            }
        }

        private void Toggle(string providerId)
        {
            if (!_hidden.Remove(providerId)) _hidden.Add(providerId);
            ApplyVisibility();
        }

        private void OnHover(int index)
        {
            if (index < 0 || _series.Count == 0)
            {
                _detail.Text = "Hover a bar for the daily breakdown.";
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

                var value = _metric == UsageMetric.Tokens
                    ? ChartMath.FormatCompact(day.Billable)
                    : ChartMath.FormatMoney(day.CostUsd.HasValue ? day.CostUsd.Value : 0);

                parts.Add(ProviderShortName.Of(_series[i].Id) + " " + value);
            }

            _detail.Text = string.Join("   ", parts.ToArray());
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
