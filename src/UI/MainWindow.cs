using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AiUsageWidget.UI
{
    public sealed class MainWindow : Window
    {
        private sealed class Countdown
        {
            public TextBlock Block;
            public DateTimeOffset? Target;
            public bool IsCycle;
        }

        /// <summary>
        /// Border used for the title-bar buttons. Border has no automation peer
        /// by default, so this subclass supplies one and exposes Invoke/Toggle.
        /// </summary>
        private sealed class GlyphButtonHost : Border
        {
            public Action InvokeAction;
            public Func<bool> ToggleStateProvider;
            public Action ToggleAction;

            protected override AutomationPeer OnCreateAutomationPeer()
            {
                return new GlyphButtonPeer(this);
            }

            public void RaiseToggleState(bool value)
            {
                var peer = UIElementAutomationPeer.FromElement(this) as GlyphButtonPeer;
                if (peer == null) return;

                var state = value
                    ? System.Windows.Automation.ToggleState.On
                    : System.Windows.Automation.ToggleState.Off;
                var previous = value
                    ? System.Windows.Automation.ToggleState.Off
                    : System.Windows.Automation.ToggleState.On;
                peer.RaisePropertyChangedEvent(
                    TogglePatternIdentifiers.ToggleStateProperty, previous, state);
            }
        }

        private sealed class GlyphButtonPeer : FrameworkElementAutomationPeer, IInvokeProvider, IToggleProvider
        {
            public GlyphButtonPeer(GlyphButtonHost owner)
                : base(owner)
            {
            }

            private GlyphButtonHost Host
            {
                get { return (GlyphButtonHost)Owner; }
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Button;
            }

            protected override string GetClassNameCore()
            {
                return "GlyphButton";
            }

            public override object GetPattern(PatternInterface patternInterface)
            {
                if (patternInterface == PatternInterface.Toggle && Host.ToggleAction != null)
                {
                    return this;
                }
                if (patternInterface == PatternInterface.Invoke && Host.InvokeAction != null)
                {
                    return this;
                }
                return base.GetPattern(patternInterface);
            }

            void IInvokeProvider.Invoke()
            {
                if (!IsEnabled()) throw new ElementNotEnabledException();
                Host.InvokeAction();
            }

            System.Windows.Automation.ToggleState IToggleProvider.ToggleState
            {
                get
                {
                    return Host.ToggleStateProvider != null && Host.ToggleStateProvider()
                        ? System.Windows.Automation.ToggleState.On
                        : System.Windows.Automation.ToggleState.Off;
                }
            }

            void IToggleProvider.Toggle()
            {
                if (!IsEnabled()) throw new ElementNotEnabledException();
                Host.ToggleAction();
            }
        }

        private sealed class ChromeButton
        {
            public GlyphButtonHost Host;
            public TextBlock Glyph;
            public Func<bool> IsActive;
            public bool Hovering;

            public void Render()
            {
                var active = IsActive != null && IsActive();
                Glyph.Foreground = (Hovering || active) ? TextBrush : MutedBrush;

                if (active)
                {
                    Host.Background = Hovering ? PinHoverBg : PinBg;
                }
                else
                {
                    Host.Background = Hovering ? HoverBg : Brushes.Transparent;
                }
            }
        }

        /// <summary>Icon glyph: visual only, never a node in the automation tree.</summary>
        private sealed class GlyphText : TextBlock
        {
            protected override AutomationPeer OnCreateAutomationPeer()
            {
                return null;
            }
        }

        // ---- palette -------------------------------------------------------
        private static readonly SolidColorBrush CardBg = Frozen(0x0A, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush ChromeBg = Frozen(0xD6, 0x11, 0x13, 0x18);
        private static readonly SolidColorBrush ChromeBorder = Frozen(0x18, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush TextBrush = Frozen(0xE6, 0xE8, 0xEC);
        private static readonly SolidColorBrush MutedBrush = Frozen(0x8A, 0x91, 0x9E);
        private static readonly SolidColorBrush FaintBrush = Frozen(0x5A, 0x62, 0x70);
        private static readonly SolidColorBrush TrackBrush = Frozen(0x14, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush HoverBg = Frozen(0x12, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush PinBg = Frozen(0x20, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush PinHoverBg = Frozen(0x2E, 0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush OkBrush = Frozen(0x3F, 0xB9, 0x50);
        private static readonly SolidColorBrush WarnBrush = Frozen(0xD2, 0x99, 0x22);
        private static readonly SolidColorBrush CritBrush = Frozen(0xF8, 0x51, 0x49);

        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");

        // ---- state ---------------------------------------------------------
        private readonly AppState _state;
        private readonly StackPanel _cards = new StackPanel();
        private readonly StackPanel _collapsedRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 8)
        };
        private readonly TextBlock _footerText = new TextBlock();
        private readonly TextBlock _collapseGlyph = new GlyphText();
        private ChromeButton _pinButton;
        private readonly List<Countdown> _countdowns = new List<Countdown>();
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _savePosition;
        private DateTimeOffset _lastResetRefresh = DateTimeOffset.MinValue;
        private bool _allowClose;

        public MainWindow(AppState state)
        {
            _state = state;

            Title = "AI Usage";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = state.Config.AlwaysOnTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = BuildChrome();
            ApplyTopmost(state.Config.AlwaysOnTop);
            ApplyPosition();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _tick.Tick += (s, e) => Tick();
            _tick.Start();

            _savePosition = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _savePosition.Tick += (s, e) =>
            {
                _savePosition.Stop();
                _state.Config.WindowX = Left;
                _state.Config.WindowY = Top;
                _state.Store.Save();
            };

            LocationChanged += (s, e) =>
            {
                _savePosition.Stop();
                _savePosition.Start();
            };

            Closing += (s, e) =>
            {
                if (_allowClose) return;
                e.Cancel = true;
                Hide();
            };

            _state.Changed += OnStateChanged;
            Closed += (s, e) =>
            {
                _state.Changed -= OnStateChanged;
                _tick.Stop();
                _savePosition.Stop();
            };
            SetCollapsed(state.Config.Collapsed, false);
            Render();
        }

        // ---- chrome --------------------------------------------------------

        private UIElement BuildChrome()
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ChromeBg,
                BorderBrush = ChromeBorder,
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };

            var stack = new StackPanel();

            var header = new Grid { Height = 38, Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "AI Usage",
                FontFamily = UiFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            var settings = GlyphButton("\uE713", "Settings", OpenSettings);
            var stats = GlyphButton("\uE9D2", "Usage statistics", OpenStatistics);
            var refresh = GlyphButton("\uE72C", "Refresh", () => _state.RefreshNow());
            _pinButton = GlyphButton(
                "\uE718", "Always on top", TogglePin, () => _state.Config.AlwaysOnTop);
            var collapse = GlyphButton("\uE70E", "Collapse", () => SetCollapsed(!IsCollapsed(), true));
            var close = GlyphButton("\uE711", "Hide to tray", Hide);

            _collapseGlyph.Text = "\uE70E";
            _collapseGlyph.FontFamily = IconFont;
            _collapseGlyph.FontSize = 11;
            _collapseGlyph.Foreground = MutedBrush;
            _collapseGlyph.TextAlignment = TextAlignment.Center;
            _collapseGlyph.VerticalAlignment = VerticalAlignment.Center;
            collapse.Host.Child = _collapseGlyph;
            collapse.Glyph = _collapseGlyph;

            Grid.SetColumn(settings.Host, 2);
            Grid.SetColumn(stats.Host, 3);
            Grid.SetColumn(refresh.Host, 4);
            Grid.SetColumn(_pinButton.Host, 5);
            Grid.SetColumn(collapse.Host, 6);
            Grid.SetColumn(close.Host, 7);
            header.Children.Add(settings.Host);
            header.Children.Add(stats.Host);
            header.Children.Add(refresh.Host);
            header.Children.Add(_pinButton.Host);
            header.Children.Add(collapse.Host);
            header.Children.Add(close.Host);

            header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch (Exception) { }
                }
            };

            stack.Children.Add(header);
            stack.Children.Add(_collapsedRow);
            stack.Children.Add(_cards);

            _footerText.FontFamily = UiFont;
            _footerText.FontSize = 9.5;
            _footerText.Foreground = FaintBrush;
            _footerText.Margin = new Thickness(14, 2, 14, 9);
            stack.Children.Add(_footerText);

            _cards.Margin = new Thickness(10, 0, 10, 0);

            outer.Child = stack;
            return outer;
        }

        private static ChromeButton GlyphButton(
            string glyph, string name, Action onClick, Func<bool> isActive = null)
        {
            var text = new GlyphText
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var host = new GlyphButtonHost
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent,
                Child = text,
                ToolTip = name,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5)
            };

            var button = new ChromeButton { Host = host, Glyph = text, IsActive = isActive };

            if (isActive == null)
            {
                host.InvokeAction = onClick;
            }
            else
            {
                host.ToggleAction = onClick;
                host.ToggleStateProvider = isActive;
            }

            AutomationProperties.SetName(host, name);
            KeyboardNavigation.SetIsTabStop(host, true);

            host.MouseEnter += (s, e) =>
            {
                button.Hovering = true;
                button.Render();
            };
            host.MouseLeave += (s, e) =>
            {
                button.Hovering = false;
                button.Render();
            };
            host.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            host.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                onClick();
            };
            host.GotKeyboardFocus += (s, e) =>
            {
                host.BorderBrush = MutedBrush;
                host.BorderThickness = new Thickness(1);
            };
            host.LostKeyboardFocus += (s, e) =>
            {
                host.BorderBrush = null;
                host.BorderThickness = new Thickness(0);
            };
            host.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    e.Handled = true;
                    onClick();
                }
            };

            button.Render();
            return button;
        }

        // ---- render --------------------------------------------------------

        private void OnStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(Render));
        }

        private void Render()
        {
            _cards.Children.Clear();
            _countdowns.Clear();

            var snapshots = _state.Snapshots;
            for (int i = 0; i < snapshots.Count; i++)
            {
                _cards.Children.Add(BuildCard(snapshots[i]));
            }

            if (snapshots.Count == 0)
            {
                _cards.Children.Add(new TextBlock
                {
                    Text = "Loading…",
                    FontFamily = UiFont,
                    FontSize = 11,
                    Foreground = MutedBrush,
                    Margin = new Thickness(4, 12, 4, 12)
                });
            }

            UpdateFooter();
            RenderCollapsedRow();
        }

        private void RenderCollapsedRow()
        {
            _collapsedRow.Children.Clear();

            var snapshots = _state.Snapshots;
            var any = false;

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot.Status == ProviderStatus.Disabled) continue;

                any = true;
                var hasData = snapshot.HasData && snapshot.Windows.Count > 0;
                var percent = hasData ? snapshot.WorstPercent : double.NaN;

                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                item.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Fill = hasData ? SeverityBrush(percent, _state.Config) : FaintBrush
                });

                item.Children.Add(new TextBlock
                {
                    Text = ProviderShortName.Of(snapshot.Id) + " " +
                           (hasData ? Math.Round(percent).ToString(CultureInfo.InvariantCulture) + "%" : "\u2014"),
                    FontFamily = UiFont,
                    FontSize = 9.5,
                    Foreground = MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });

                _collapsedRow.Children.Add(item);
            }

            if (!any)
            {
                _collapsedRow.Children.Add(new TextBlock
                {
                    Text = "No data",
                    FontFamily = UiFont,
                    FontSize = 9.5,
                    Foreground = FaintBrush
                });
            }
        }

        private Border BuildCard(ProviderSnapshot snapshot)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = CardBg,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 0)
            };

            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = snapshot.Name,
                FontFamily = UiFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = snapshot.Status == ProviderStatus.Disabled ? FaintBrush : TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(name);

            if (!string.IsNullOrEmpty(snapshot.Plan))
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = Frozen(0x1A, 0xFF, 0xFF, 0xFF),
                    Padding = new Thickness(5, 1, 5, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = snapshot.Plan,
                    FontFamily = UiFont,
                    FontSize = 9,
                    Foreground = MutedBrush
                };
                Grid.SetColumn(badge, 1);
                head.Children.Add(badge);
            }

            stack.Children.Add(head);

            if (snapshot.Status != ProviderStatus.Ok &&
                snapshot.Status != ProviderStatus.Disabled)
            {
                stack.Children.Add(BuildNote(FreshnessText(snapshot), FaintBrush));
            }

            for (int i = 0; i < snapshot.Windows.Count; i++)
            {
                stack.Children.Add(BuildWindowRow(snapshot.Windows[i]));
            }

            if (snapshot.Status == ProviderStatus.Disabled)
            {
                stack.Children.Add(BuildNote("Disabled in Settings.", FaintBrush));
            }
            else if (snapshot.Status != ProviderStatus.Ok && !string.IsNullOrEmpty(snapshot.ErrorMessage))
            {
                var brush = (snapshot.Status == ProviderStatus.Stale || snapshot.Status == ProviderStatus.StaleAuth)
                    ? WarnBrush
                    : CritBrush;
                stack.Children.Add(BuildNote(snapshot.ErrorMessage, brush));

                if (snapshot.Status == ProviderStatus.NoCredentials || snapshot.Status == ProviderStatus.StaleAuth)
                {
                    stack.Children.Add(LinkButton("Open Settings", OpenSettings));
                }
            }

            if (_state.Config.ShowConsumption && snapshot.Consumption.Count > 0)
            {
                stack.Children.Add(BuildConsumption(snapshot));
            }

            card.Child = stack;
            AutomationProperties.SetName(card, snapshot.Name + " usage");
            return card;
        }

        private string FreshnessText(ProviderSnapshot snapshot)
        {
            if (snapshot.FetchedAt == DateTimeOffset.MinValue)
            {
                return "No successful update yet.";
            }

            var local = snapshot.FetchedAt.ToLocalTime();
            return "Last success " + local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) +
                   " (" + FormatAge(DateTimeOffset.UtcNow - snapshot.FetchedAt) + " ago).";
        }

        private static string FormatAge(TimeSpan span)
        {
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return ((int)span.TotalMinutes) + "m";
            if (span.TotalDays < 1) return ((int)span.TotalHours) + "h";
            return ((int)span.TotalDays) + "d";
        }

        private UIElement BuildWindowRow(UsageWindow window)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

            var label = new TextBlock
            {
                Text = window.Label,
                FontFamily = UiFont,
                FontSize = 10,
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(label);

            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            var track = new Border { Background = TrackBrush, CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.001, window.UsedPercent), GridUnitType.Star)
            });
            bar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.001, 100 - window.UsedPercent), GridUnitType.Star)
            });
            var fill = new Border
            {
                Background = SeverityBrush(window.UsedPercent, _state.Config),
                CornerRadius = new CornerRadius(3)
            };
            Grid.SetColumn(fill, 0);
            bar.Children.Add(track);
            bar.Children.Add(fill);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);

            var percent = new TextBlock
            {
                Text = Math.Round(window.UsedPercent).ToString(CultureInfo.InvariantCulture) + "%",
                FontFamily = UiFont,
                FontSize = 10,
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(percent, 2);
            grid.Children.Add(percent);

            var reset = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 9.5,
                Foreground = MutedBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);

            if (window.IsCycle && window.ResetsAt.HasValue)
            {
                reset.Text = window.ResetsAt.Value.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
            }
            else if (window.ResetsAt.HasValue)
            {
                reset.Text = FormatRemaining(window.ResetsAt.Value - DateTimeOffset.UtcNow);
                _countdowns.Add(new Countdown { Block = reset, Target = window.ResetsAt, IsCycle = false });
            }
            else
            {
                reset.Text = "\u2014";
            }

            return grid;
        }

        private UIElement BuildConsumption(ProviderSnapshot snapshot)
        {
            var text = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 9.5,
                Foreground = FaintBrush,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            string today = null;
            string week = null;
            string month = null;

            for (int i = 0; i < snapshot.Consumption.Count; i++)
            {
                var item = snapshot.Consumption[i];
                if (item.Period == "today") today = FormatConsumption(item);
                else if (item.Period == "7d") week = FormatConsumption(item);
                else if (item.Period == "30d") month = FormatConsumption(item);
            }

            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(today)) parts.Add("today " + today);
            if (!string.IsNullOrEmpty(week)) parts.Add("7d " + week);
            if (!string.IsNullOrEmpty(month)) parts.Add("30d " + month);
            text.Text = string.Join("  \u00b7  ", parts.ToArray());

            if (snapshot.ConsumptionStale)
            {
                text.Text = "stale  \u00b7  " + text.Text;
                text.Foreground = WarnBrush;
            }
            else if (!string.IsNullOrEmpty(snapshot.ConsumptionError))
            {
                text.ToolTip = "Consumption could not be refreshed (" + snapshot.ConsumptionError + ").";
            }

            return text;
        }

        private static string FormatConsumption(Consumption item)
        {
            var parts = new List<string>(2);
            if (item.CostUsd.HasValue)
            {
                parts.Add("$" + item.CostUsd.Value.ToString("0.00", CultureInfo.InvariantCulture));
            }
            var tokens = item.TotalTokens;
            if (tokens > 0)
            {
                parts.Add(FormatTokens(tokens) + " tok");
            }
            else if (!item.CostUsd.HasValue)
            {
                parts.Add("0 tok");
            }
            return parts.Count == 0 ? "\u2014" : string.Join(" ", parts.ToArray());
        }

        private static string FormatTokens(long tokens)
        {
            if (tokens >= 1000000) return (tokens / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (tokens >= 1000) return (tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        private static TextBlock BuildNote(string message, Brush brush)
        {
            return new TextBlock
            {
                Text = message,
                FontFamily = UiFont,
                FontSize = 9.5,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
        }

        private static Border LinkButton(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 9.5,
                Foreground = MutedBrush,
                Cursor = Cursors.Hand
            };

            var host = new Border
            {
                Background = Brushes.Transparent,
                Child = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = text
            };

            host.MouseEnter += (s, e) => label.TextDecorations = TextDecorations.Underline;
            host.MouseLeave += (s, e) => label.TextDecorations = null;
            host.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            host.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                onClick();
            };

            return host;
        }

        private void OpenSettings()
        {
            var settings = new SettingsWindow(_state, this);
            settings.ShowDialog();
        }

        private void OpenStatistics()
        {
            var statistics = new StatisticsWindow(_state, this);
            statistics.ShowDialog();
        }

        private void ShowSaveError(string message, string code)
        {
            var detail = string.IsNullOrEmpty(code) ? string.Empty : " (" + code + ")";
            MessageBox.Show(this, message + detail, AppInfo.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---- interaction ---------------------------------------------------

        private void TogglePin()
        {
            _state.Config.AlwaysOnTop = !_state.Config.AlwaysOnTop;
            var saved = _state.Store.Save();

            // Save() rolls the in-memory config back on failure, so reading it
            // again after the call keeps the window and the pin state honest.
            ApplyTopmost(_state.Config.AlwaysOnTop);

            if (!saved.Success)
            {
                ShowSaveError("Always on top was not changed.", saved.ErrorCode);
            }
        }

        public void SetTopmost(bool value)
        {
            ApplyTopmost(value);
        }

        private void ApplyTopmost(bool value)
        {
            Topmost = value;

            if (_pinButton == null) return;

            _pinButton.Host.ToolTip = value ? "Always on top: on" : "Always on top: off";
            _pinButton.Render();
            _pinButton.Host.RaiseToggleState(value);
        }

        private bool IsCollapsed()
        {
            return _cards.Visibility == Visibility.Collapsed;
        }

        public void SetCollapsed(bool collapsed, bool persist)
        {
            _cards.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _footerText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _collapsedRow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            _collapseGlyph.Text = collapsed ? "\uE70D" : "\uE70E";

            if (persist)
            {
                _state.Config.Collapsed = collapsed;
                var saved = _state.Store.Save();
                if (!saved.Success)
                {
                    ApplyCollapsedVisual(_state.Config.Collapsed);
                    ShowSaveError("The collapsed state was not saved.", saved.ErrorCode);
                }
            }
        }

        private void ApplyCollapsedVisual(bool collapsed)
        {
            _cards.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _footerText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            _collapsedRow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            _collapseGlyph.Text = collapsed ? "\uE70D" : "\uE70E";
        }

        public void ShowFromTray()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            EnsureVisible();
            Activate();
        }

        public void CloseForReal()
        {
            _allowClose = true;
            Close();
        }

        /// <summary>Development helper: renders the widget to a PNG for visual review.</summary>
        public void SaveScreenshot(string path)
        {
            Screenshot.Save(this, path);
        }

        private void Tick()
        {
            var now = DateTimeOffset.UtcNow;
            var resetPassed = false;

            for (int i = 0; i < _countdowns.Count; i++)
            {
                var item = _countdowns[i];
                if (!item.Target.HasValue) continue;

                var remaining = item.Target.Value - now;
                if (remaining.TotalSeconds <= 0)
                {
                    item.Block.Text = "resetting…";
                    resetPassed = true;
                }
                else
                {
                    item.Block.Text = FormatRemaining(remaining);
                }
            }

            UpdateFooter();

            if (resetPassed && (now - _lastResetRefresh).TotalMinutes > 2)
            {
                _lastResetRefresh = now;
                _state.RefreshAfterReset();
            }

            EnsureVisible();
        }

        private void UpdateFooter()
        {
            if (_state.Refreshing)
            {
                _footerText.Text = "Refreshing\u2026";
                return;
            }

            if (_state.LastSuccessfulRefresh == DateTimeOffset.MinValue)
            {
                if (AllProvidersDisabled())
                {
                    _footerText.Text = "All providers disabled in Settings";
                    return;
                }

                _footerText.Text = _state.LastAttempt == DateTimeOffset.MinValue
                    ? "Waiting for first refresh"
                    : "No successful refresh yet";
                return;
            }

            _footerText.Text = "Updated " +
                               _state.LastSuccessfulRefresh.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) +
                               "  \u00b7  every " + Math.Max(1, _state.Config.QuotaMinutes) + " min" +
                               (_state.LastRunHadErrors ? "  \u00b7  retrying" : string.Empty);
        }

        // ---- helpers -------------------------------------------------------

        private void ApplyPosition()
        {
            var x = _state.Config.WindowX;
            var y = _state.Config.WindowY;

            if (x.HasValue && y.HasValue &&
                DesktopPlacement.IsVisible(x.Value, y.Value, Width, 160))
            {
                Left = x.Value;
                Top = y.Value;
                return;
            }

            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Top + 24;
        }

        /// <summary>
        /// Called from the UI tick. Recovers the widget after a monitor is
        /// disconnected or the resolution changes, and never leaves it entirely
        /// outside a real monitor's usable area.
        /// </summary>
        public void EnsureVisible()
        {
            if (!IsVisible) return;

            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : 160;

            if (DesktopPlacement.IsVisible(Left, Top, width, height)) return;

            var area = SystemParameters.WorkArea;
            Left = area.Right - width - 24;
            Top = area.Top + 24;
        }

        private bool AllProvidersDisabled()
        {
            var snapshots = _state.Snapshots;
            if (snapshots.Count == 0) return false;

            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].Status != ProviderStatus.Disabled) return false;
            }

            return true;
        }

        private static string FormatRemaining(TimeSpan span)
        {
            if (span.TotalSeconds <= 0) return "resetting…";
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + "d " + span.Hours + "h";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
            return Math.Max(1, (int)span.TotalMinutes) + "m";
        }

        private static Brush SeverityBrush(double percent, AppConfig config)
        {
            if (percent >= config.CriticalPercent) return CritBrush;
            if (percent >= config.WarnPercent) return WarnBrush;
            return OkBrush;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            return Frozen(0xFF, r, g, b);
        }

        private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
