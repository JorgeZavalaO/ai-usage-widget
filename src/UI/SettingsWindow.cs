using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    public sealed class SettingsWindow : Window
    {
        private readonly AppState _state;
        private readonly PasswordBox _keyBox = new PasswordBox();
        private readonly TextBlock _keyStatus = new TextBlock();

        public SettingsWindow(AppState state, Window owner)
        {
            _state = state;

            Title = "AI Usage â€” Settings";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Setting Owner to a window that was never shown throws, which is the
            // normal case when the widget starts hidden in the tray.
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

            Content = Build();
            RefreshKeyStatus();
        }

        private UIElement Build()
        {
            var stack = new StackPanel { Margin = new Thickness(16) };
            // Window.Foreground does not propagate to TextBlock in dark system
            // themes; TextElement.Foreground does.
            System.Windows.Documents.TextElement.SetForeground(stack, Brushes.Black);

            stack.Children.Add(new TextBlock
            {
                Text = "OpenCode Go API key",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 6)
            });

            _keyBox.PasswordChar = '\u2022';
            _keyBox.Padding = new Thickness(6, 4, 6, 4);
            _keyBox.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(_keyBox);

            _keyStatus.FontSize = 10;
            _keyStatus.Foreground = Brushes.Gray;
            _keyStatus.TextWrapping = TextWrapping.Wrap;
            _keyStatus.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(_keyStatus);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var save = new Button
            {
                Content = "Save key",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            save.Click += (s, e) => SaveKey();
            buttons.Children.Add(save);

            var clear = new Button
            {
                Content = "Clear key",
                Padding = new Thickness(10, 4, 10, 4)
            };
            clear.Click += (s, e) =>
            {
                _state.Store.SetOpenCodeKey(null);
                _keyBox.Clear();
                RefreshKeyStatus();
                _state.RefreshNow();
            };
            buttons.Children.Add(clear);

            stack.Children.Add(buttons);

            stack.Children.Add(new TextBlock
            {
                Text = "The key is encrypted with Windows DPAPI for this user and is only sent to opencode.ai. " +
                       "Nothing is written to logs.",
                FontSize = 10,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var startup = new CheckBox
            {
                Content = new TextBlock { Text = "Start with Windows", Foreground = Brushes.Black },
                IsChecked = _state.Config.LaunchAtLogin,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 6)
            };
            startup.Checked += (s, e) =>
            {
                _state.Config.LaunchAtLogin = true;
                _state.Store.Save();
                Autostart.Set(true, ExecutablePath());
            };
            startup.Unchecked += (s, e) =>
            {
                _state.Config.LaunchAtLogin = false;
                _state.Store.Save();
                Autostart.Set(false, ExecutablePath());
            };
            stack.Children.Add(startup);

            var consumption = new CheckBox
            {
                Content = new TextBlock { Text = "Show local token consumption", Foreground = Brushes.Black },
                IsChecked = _state.Config.ShowConsumption,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 12)
            };
            consumption.Checked += (s, e) =>
            {
                _state.Config.ShowConsumption = true;
                _state.Store.Save();
                _state.RefreshNow();
            };
            consumption.Unchecked += (s, e) =>
            {
                _state.Config.ShowConsumption = false;
                _state.Store.Save();
                _state.RefreshNow();
            };
            stack.Children.Add(consumption);

            var close = new Button
            {
                Content = "Close",
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            stack.Children.Add(close);

            return stack;
        }

        private void SaveKey()
        {
            var key = _keyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) return;

            if (!ConfigStore.EncryptionAvailable)
            {
                MessageBox.Show(this,
                    "Windows DPAPI is not available. The key was not saved.",
                    "AI Usage", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _state.Store.SetOpenCodeKey(key.Trim());
            _keyBox.Clear();
            RefreshKeyStatus();
            _state.RefreshNow();
        }

        private void RefreshKeyStatus()
        {
            _keyStatus.Text = _state.Store.HasOpenCodeKey
                ? "A key is stored. Paste a new one to replace it."
                : "No key stored. The widget uses the key from OpenCode v2 automatically when it is running.";
        }

        /// <summary>Development helper: renders the dialog to a PNG for visual review.</summary>
        public void SaveScreenshot(string path)
        {
            Screenshot.Save(this, path);
        }

        private static string ExecutablePath()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception)
            {
                return System.Reflection.Assembly.GetEntryAssembly().Location;
            }
        }
    }
}


