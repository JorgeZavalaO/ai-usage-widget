using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AiUsageWidget.UI
{
    public sealed class Tray : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private sealed class IconSet
        {
            public Icon Icon;
            public IntPtr Handle;
        }

        private readonly AppState _state;
        private readonly MainWindow _window;
        private readonly System.Windows.Forms.NotifyIcon _notify;
        private readonly System.Windows.Forms.ToolStripMenuItem _showItem;
        private readonly System.Windows.Forms.ToolStripMenuItem _topItem;
        private readonly System.Windows.Forms.ToolStripMenuItem _startupItem;

        private readonly IconSet _ok;
        private readonly IconSet _warn;
        private readonly IconSet _crit;
        private readonly IconSet _unknown;

        private bool _disposed;

        public Tray(AppState state, MainWindow window)
        {
            _state = state;
            _window = window;

            _ok = CreateIcon(Color.FromArgb(0x3F, 0xB9, 0x50));
            _warn = CreateIcon(Color.FromArgb(0xD2, 0x99, 0x22));
            _crit = CreateIcon(Color.FromArgb(0xF8, 0x51, 0x49));
            _unknown = CreateIcon(Color.FromArgb(0x8A, 0x91, 0x9E));

            var menu = new System.Windows.Forms.ContextMenuStrip();

            _showItem = new System.Windows.Forms.ToolStripMenuItem("Show");
            _showItem.Click += (s, e) => ToggleWindow();
            menu.Items.Add(_showItem);

            var refreshItem = new System.Windows.Forms.ToolStripMenuItem("Refresh now");
            refreshItem.Click += (s, e) => _state.RefreshNow();
            menu.Items.Add(refreshItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            _topItem = new System.Windows.Forms.ToolStripMenuItem("Always on top");
            _topItem.CheckOnClick = false;
            _topItem.Click += (s, e) =>
            {
                var value = !_state.Config.AlwaysOnTop;
                _state.Config.AlwaysOnTop = value;
                var saved = _state.Store.Save();
                _window.SetTopmost(_state.Config.AlwaysOnTop);
                if (!saved.Success)
                {
                    System.Windows.MessageBox.Show(
                        "Always on top was not saved (" + saved.ErrorCode + ").",
                        AppInfo.Product,
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(_topItem);

            _startupItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows");
            _startupItem.Click += (s, e) =>
            {
                var value = !_state.Autostart.IsEnabledFor(ExecutablePath());
                var result = LoginItemToggle.Apply(
                    _state.Config,
                    _state.Store,
                    _state.Autostart,
                    value,
                    ExecutablePath());

                if (!result.Success)
                {
                    System.Windows.MessageBox.Show(
                        "Launch at login was not changed (" + result.ErrorCode + ").",
                        AppInfo.Product,
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(_startupItem);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings…");
            settingsItem.Click += (s, e) => OpenSettings();
            menu.Items.Add(settingsItem);

            var quitItem = new System.Windows.Forms.ToolStripMenuItem("Quit");
            quitItem.Click += (s, e) =>
            {
                window.CloseForReal();
                System.Windows.Application.Current.Shutdown();
            };
            menu.Items.Add(quitItem);

            menu.Opening += (s, e) =>
            {
                _showItem.Text = window.IsVisible ? "Hide" : "Show";
                _topItem.Checked = _state.Config.AlwaysOnTop;
                _startupItem.Checked = _state.Autostart.IsEnabledFor(ExecutablePath());
            };

            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = _unknown.Icon,
                Text = "AI Usage",
                Visible = true,
                ContextMenuStrip = menu
            };
            _notify.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleWindow();
            };

            _state.Changed += OnChanged;
            Refresh();
        }

        private void OnChanged()
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher != null) dispatcher.BeginInvoke(new Action(Refresh));
        }

        private void ToggleWindow()
        {
            if (_window.IsVisible) _window.Hide();
            else _window.ShowFromTray();
        }

        private void OpenSettings()
        {
            var settings = new SettingsWindow(_state, _window);
            settings.ShowDialog();
        }

        private void Refresh()
        {
            if (_disposed) return;

            var snapshots = _state.Snapshots;
            double worst = -1;
            var parts = new List<string>(3);

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot.Status == ProviderStatus.Disabled) continue;

                var shortName = ProviderShortName.Of(snapshot.Id);
                if (snapshot.HasData && snapshot.Windows.Count > 0)
                {
                    var value = snapshot.WorstPercent;
                    if (value > worst) worst = value;
                    parts.Add(shortName + " " + Math.Round(value).ToString() + "%");
                }
                else
                {
                    parts.Add(shortName + " \u2014");
                }
            }

            var icon = _unknown;
            if (worst >= _state.Config.CriticalPercent) icon = _crit;
            else if (worst >= _state.Config.WarnPercent) icon = _warn;
            else if (worst >= 0) icon = _ok;

            _notify.Icon = icon.Icon;

            var text = parts.Count == 0 ? "AI Usage" : string.Join(" · ", parts.ToArray());
            _notify.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
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

        private static IconSet CreateIcon(Color color)
        {
            var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, 5, 5, 22, 22);
                }
                using (var ring = new Pen(Color.FromArgb(60, 255, 255, 255), 1.5f))
                {
                    graphics.DrawEllipse(ring, 5, 5, 22, 22);
                }
            }

            var handle = bitmap.GetHicon();
            var icon = Icon.FromHandle(handle);
            bitmap.Dispose();

            return new IconSet { Icon = icon, Handle = handle };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _state.Changed -= OnChanged;
            _notify.Visible = false;
            _notify.Dispose();

            DestroyIcon(_ok.Handle);
            DestroyIcon(_warn.Handle);
            DestroyIcon(_crit.Handle);
            DestroyIcon(_unknown.Handle);
        }
    }
}
