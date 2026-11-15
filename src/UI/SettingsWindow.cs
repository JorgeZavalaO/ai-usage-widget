using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiUsageWidget.UI
{
    public sealed class SettingsWindow : Window
    {
        private readonly AppState _state;
        private readonly PasswordBox _keyBox = new PasswordBox();
        private readonly TextBlock _keyStatus = new TextBlock();
        private Button _saveButton;
        private Button _clearButton;
        private CheckBox _startup;
        private CheckBox _consumption;
        private CheckBox _openCode;
        private CheckBox _codex;
        private CheckBox _claude;
        private bool _updatingToggles;

        public SettingsWindow(AppState state, Window owner)
        {
            _state = state;

            Title = "AI Usage - Settings";
            Width = 440;
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
            Background = Theme.Surface;
            Foreground = Theme.Text;
            FontFamily = Theme.UiFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = Fit(Build());
            StyleKeyStatus();
            ReconcileStartup();
            RefreshButtons();
        }

        /// <summary>
        /// The dialog is taller than some laptop work areas at 125% scaling. Cap it
        /// and let the content scroll instead of clipping the footer.
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
            var stack = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
            System.Windows.Documents.TextElement.SetForeground(stack, Theme.Text);

            // ------------------------------------------------------------ connections
            stack.Children.Add(Theme.Section("Connections"));
            stack.Children.Add(Theme.Card(BuildKeySection()));

            Theme.Spacer(stack, 14);

            // -------------------------------------------------------------- providers
            stack.Children.Add(Theme.Section("Providers"));

            var providers = new StackPanel();
            AddProviderRow(providers,
                "OpenCode Go",
                "5h, weekly and monthly windows, plus local token usage",
                _state.Config.OpenCodeEnabled,
                box => { _openCode = box; },
                enabled => ApplyProvider("opencode-go", enabled));
            AddProviderRow(providers,
                "ChatGPT / Codex",
                "5h and weekly windows, plus local sessions",
                _state.Config.CodexEnabled,
                box => { _codex = box; },
                enabled => ApplyProvider("codex", enabled));
            AddProviderRow(providers,
                "Claude",
                "5h and weekly windows, plus local sessions",
                _state.Config.ClaudeEnabled,
                box => { _claude = box; },
                enabled => ApplyProvider("claude", enabled));

            stack.Children.Add(Theme.Card(providers));

            Theme.Spacer(stack, 14);

            // --------------------------------------------------------------- behavior
            stack.Children.Add(Theme.Section("Behavior"));
            stack.Children.Add(Theme.Card(BuildBehaviorSection()));

            Theme.Spacer(stack, 12);
            stack.Children.Add(Theme.Separator());
            Theme.Spacer(stack, 8);

            // ----------------------------------------------------------------- footer
            stack.Children.Add(BuildFooter());

            return stack;
        }

        private UIElement BuildKeySection()
        {
            var stack = new StackPanel();

            stack.Children.Add(Theme.Label("OpenCode Go API key"));

            var hint = Theme.Caption("Overrides the key discovered from the OpenCode v2 service.");
            hint.Margin = new Thickness(0, 2, 0, 8);
            stack.Children.Add(hint);

            _keyBox.PasswordChar = '\u2022';
            _keyBox.Height = 32;
            _keyBox.Padding = new Thickness(8, 5, 8, 5);
            _keyBox.FontSize = 12;
            _keyBox.FontFamily = Theme.UiFont;
            _keyBox.Background = Theme.CardBg;
            _keyBox.BorderBrush = Theme.BorderStrong;
            _keyBox.BorderThickness = new Thickness(1);
            _keyBox.VerticalContentAlignment = VerticalAlignment.Center;
            _keyBox.PasswordChanged += (s, e) => RefreshButtons();
            _keyBox.KeyDown += OnKeyDown;
            AutomationProperties.SetName(_keyBox, "OpenCode Go API key");
            stack.Children.Add(_keyBox);

            _keyStatus.FontFamily = Theme.UiFont;
            _keyStatus.FontSize = 10.5;
            _keyStatus.TextWrapping = TextWrapping.Wrap;
            _keyStatus.MinHeight = 16;
            _keyStatus.Margin = new Thickness(0, 5, 0, 7);
            stack.Children.Add(_keyStatus);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            _saveButton = Theme.Button("Save key", Theme.ButtonKind.Primary, SaveKey);
            _saveButton.Margin = new Thickness(0, 0, 8, 0);
            _clearButton = Theme.Button("Clear key", Theme.ButtonKind.Danger, ClearKey);

            actions.Children.Add(_saveButton);
            actions.Children.Add(_clearButton);
            stack.Children.Add(actions);

            var note = Theme.Footnote("Encrypted with Windows DPAPI for this user and sent only to opencode.ai.");
            note.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(note);

            return stack;
        }

        private UIElement BuildBehaviorSection()
        {
            var stack = new StackPanel();

            _startup = ThemeCheckBox("Start with Windows", "Opens hidden in the tray when you sign in.", _state.Config.LaunchAtLogin);
            _startup.Checked += (s, e) => ApplyStartup(true);
            _startup.Unchecked += (s, e) => ApplyStartup(false);
            stack.Children.Add(_startup);

            _consumption = ThemeCheckBox(
                "Show local token consumption",
                "Adds today, 7-day and 30-day totals under each provider.",
                _state.Config.ShowConsumption);
            _consumption.Margin = new Thickness(0, 10, 0, 0);
            _consumption.Checked += (s, e) => ApplyConsumption(true);
            _consumption.Unchecked += (s, e) => ApplyConsumption(false);
            stack.Children.Add(_consumption);

            return stack;
        }

        private static CheckBox ThemeCheckBox(string title, string description, bool value)
        {
            var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            text.Children.Add(Theme.Body(title));

            var caption = Theme.Caption(description);
            caption.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(caption);

            // The default template leaves only a few pixels between the glyph and
            // its content; the content margin restores a comfortable gap.
            return new CheckBox
            {
                Content = text,
                IsChecked = value,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 2),
                FontFamily = Theme.UiFont,
                Foreground = Theme.Text
            };
        }

        private static void AddProviderRow(
            StackPanel host,
            string name,
            string description,
            bool value,
            Action<CheckBox> remember,
            Action<bool> changed)
        {
            var box = ThemeCheckBox(name, description, value);
            box.Margin = new Thickness(0, host.Children.Count == 0 ? 0 : 10, 0, 0);

            remember(box);
            box.Checked += (s, e) => changed(true);
            box.Unchecked += (s, e) => changed(false);
            host.Children.Add(box);
        }

        private UIElement BuildFooter()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var version = Theme.Footnote(AppInfo.Product + " " + AppInfo.VersionWithPrefix);
            version.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(version);

            var close = Theme.Button("Close", Theme.ButtonKind.Secondary, Close);
            close.IsCancel = true;
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            return grid;
        }

        // ------------------------------------------------------------------- actions

        private void SaveKey()
        {
            var key = _keyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) return;

            var result = _state.Store.SetOpenCodeKey(key.Trim());
            if (!result.Success)
            {
                ShowStatus("The key was not saved" + Code(result.ErrorCode) + ".", Theme.Danger);
                return;
            }

            _keyBox.Clear();
            ShowStatus("Key saved. Refreshing usage now.", Theme.Success);
            RefreshButtons();
            _state.RefreshNow();
        }

        private void ClearKey()
        {
            var confirm = MessageBox.Show(
                this,
                "Remove the stored OpenCode Go key? The widget will fall back to automatic discovery from the OpenCode v2 service.",
                AppInfo.Product,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (confirm != MessageBoxResult.OK) return;

            var result = _state.Store.SetOpenCodeKey(null);
            if (!result.Success)
            {
                ShowStatus("The stored key could not be removed" + Code(result.ErrorCode) + ".", Theme.Danger);
                return;
            }

            _keyBox.Clear();
            ShowStatus("Key cleared.", Theme.Muted);
            RefreshButtons();
            _state.RefreshNow();
        }

        private void ApplyProvider(string id, bool enabled)
        {
            if (_updatingToggles) return;

            var config = _state.Config;
            var previous = ReadProvider(config, id);
            WriteProvider(config, id, enabled);

            var saved = _state.Store.Save();
            if (!saved.Success)
            {
                WriteProvider(config, id, previous);
                RevertProviderBox(id, previous);
                ShowStatus("The provider was not changed" + Code(saved.ErrorCode) + ".", Theme.Danger);
                return;
            }

            _state.ScheduleTimers();
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
            ShowStatus("Launch at login was not changed" + Code(result.ErrorCode) + ".", Theme.Danger);
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
                ShowStatus("The setting was not changed" + Code(result.ErrorCode) + ".", Theme.Danger);
                return;
            }

            _state.ScheduleTimers();
            _state.RefreshNow();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_saveButton.IsEnabled) SaveKey();
        }

        // -------------------------------------------------------------------- state

        private void RefreshButtons()
        {
            _saveButton.IsEnabled = _keyBox.Password.Length > 0;
            _clearButton.IsEnabled = _state.Store.HasOpenCodeKey;

            if (_keyBox.Password.Length == 0 && _keyStatus.Tag == null)
            {
                StyleKeyStatus();
            }
        }

        private void StyleKeyStatus()
        {
            _keyStatus.Text = _state.Store.HasOpenCodeKey
                ? "A key is stored. Paste a new one to replace it."
                : "No key stored. Automatic discovery from OpenCode v2 is used.";
            _keyStatus.Foreground = _state.Store.HasOpenCodeKey ? Theme.Text : Theme.Muted;
            _keyStatus.Tag = null;
        }

        private void ShowStatus(string message, Brush brush)
        {
            _keyStatus.Text = message;
            _keyStatus.Foreground = brush;
            _keyStatus.Tag = "message";
        }

        private static string Code(string code)
        {
            return string.IsNullOrEmpty(code) ? string.Empty : " (" + code + ")";
        }

        private static bool ReadProvider(AppConfig config, string id)
        {
            if (id == "opencode-go") return config.OpenCodeEnabled;
            if (id == "codex") return config.CodexEnabled;
            return config.ClaudeEnabled;
        }

        private static void WriteProvider(AppConfig config, string id, bool value)
        {
            if (id == "opencode-go") config.OpenCodeEnabled = value;
            else if (id == "codex") config.CodexEnabled = value;
            else config.ClaudeEnabled = value;
        }

        private void RevertProviderBox(string id, bool value)
        {
            _updatingToggles = true;
            if (id == "opencode-go" && _openCode != null) _openCode.IsChecked = value;
            if (id == "codex" && _codex != null) _codex.IsChecked = value;
            if (id == "claude" && _claude != null) _claude.IsChecked = value;
            _updatingToggles = false;
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
                    ShowStatus("Launch at login could not be repaired" + Code(repair.ErrorCode) + ".", Theme.Danger);
                }
            }

            if (_state.Config.LaunchAtLogin != actual)
            {
                _state.Config.LaunchAtLogin = actual;
                var saved = _state.Store.Save();
                if (!saved.Success)
                {
                    actual = _state.Config.LaunchAtLogin;
                    ShowStatus("The launch-at-login state could not be saved" + Code(saved.ErrorCode) + ".", Theme.Danger);
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






