using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    public sealed class SettingsWindow : Window
    {
        private readonly AppState _state;
        private readonly PasswordBox _keyBox = new PasswordBox();
        private readonly TextBlock _keyStatus = new TextBlock();
        private CheckBox _startup;
        private CheckBox _consumption;
        private bool _updatingToggles;

        public SettingsWindow(AppState state, Window owner)
        {
            _state = state;

            Title = "AI Usage - Settings";
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
            ReconcileStartup();
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
            AutomationProperties.SetName(_keyBox, "OpenCode Go API key");
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
            clear.Click += (s, e) => ClearKey();
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

            _startup = new CheckBox
            {
                Content = new TextBlock { Text = "Start with Windows", Foreground = Brushes.Black },
                IsChecked = _state.Config.LaunchAtLogin,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _startup.Checked += (s, e) => ApplyStartup(true);
            _startup.Unchecked += (s, e) => ApplyStartup(false);
            stack.Children.Add(_startup);

            _consumption = new CheckBox
            {
                Content = new TextBlock { Text = "Show local token consumption", Foreground = Brushes.Black },
                IsChecked = _state.Config.ShowConsumption,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _consumption.Checked += (s, e) => ApplyConsumption(true);
            _consumption.Unchecked += (s, e) => ApplyConsumption(false);
            stack.Children.Add(_consumption);

            stack.Children.Add(new TextBlock
            {
                Text = AppInfo.Product + " " + AppInfo.VersionWithPrefix,
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            });

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

            var result = _state.Store.SetOpenCodeKey(key.Trim());
            if (!result.Success)
            {
                ShowError("The key was not saved.", result.ErrorCode);
                return;
            }

            _keyBox.Clear();
            RefreshKeyStatus();
            _state.RefreshNow();
        }

        private void ClearKey()
        {
            var result = _state.Store.SetOpenCodeKey(null);
            if (!result.Success)
            {
                ShowError("The stored key could not be removed.", result.ErrorCode);
                return;
            }

            _keyBox.Clear();
            RefreshKeyStatus();
            _state.RefreshNow();
        }

        private void ApplyStartup(bool enabled)
        {
            if (_updatingToggles) return;

            var result = LoginItemToggle.Apply(
                _state.Config,
                _state.Store,
                _state.Autostart,
                enabled,
                ExecutablePath());

            if (result.Success) return;

            _updatingToggles = true;
            _startup.IsChecked = _state.Config.LaunchAtLogin;
            _updatingToggles = false;
            ShowError("Launch at login was not changed.", result.ErrorCode);
        }

        private void ApplyConsumption(bool enabled)
        {
            if (_updatingToggles) return;

            _state.Config.ShowConsumption = enabled;
            var result = _state.Store.Save();
            if (!result.Success)
            {
                _updatingToggles = true;
                _consumption.IsChecked = _state.Config.ShowConsumption;
                _updatingToggles = false;
                ShowError("The setting was not changed.", result.ErrorCode);
                return;
            }

            _state.ScheduleTimers();
            _state.RefreshNow();
        }

        private void ShowError(string message, string code)
        {
            var detail = string.IsNullOrEmpty(code) ? string.Empty : " (" + code + ")";
            if (IsVisible)
            {
                MessageBox.Show(this, message + detail, AppInfo.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message + detail, AppInfo.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshKeyStatus()
        {
            _keyStatus.Text = _state.Store.HasOpenCodeKey
                ? "A key is stored. Paste a new one to replace it."
                : "No key stored. The widget uses the key from OpenCode v2 automatically when it is running.";
        }

        /// <summary>
        /// The registry is the real source of truth for launch-at-login. A moved
        /// portable executable leaves a stale Run value, so repair it when the
        /// setting is enabled, and never show a checkbox state the registry does
        /// not support.
        /// </summary>
        private void ReconcileStartup()
        {
            var executablePath = ExecutablePath();
            var actual = _state.Autostart.IsEnabledFor(executablePath);

            if (_state.Config.LaunchAtLogin && !actual)
            {
                var repair = LoginItemToggle.Apply(
                    _state.Config,
                    _state.Store,
                    _state.Autostart,
                    true,
                    executablePath);

                actual = repair.Success && _state.Autostart.IsEnabledFor(executablePath);
                if (!repair.Success)
                {
                    ShowError("Launch at login could not be repaired.", repair.ErrorCode);
                }
            }

            if (_state.Config.LaunchAtLogin != actual)
            {
                _state.Config.LaunchAtLogin = actual;
                var saved = _state.Store.Save();
                if (!saved.Success)
                {
                    ShowError("The launch-at-login state could not be saved.", saved.ErrorCode);
                    actual = _state.Config.LaunchAtLogin;
                }
            }

            _updatingToggles = true;
            _startup.IsChecked = actual;
            _updatingToggles = false;
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
