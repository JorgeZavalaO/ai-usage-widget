using System;
using System.Threading;
using System.Windows;
using AiUsageWidget.UI;

namespace AiUsageWidget
{
    internal static class Program
    {
        private const string MutexName = "Local\\AIUsageWidget.SingleInstance";
        private const string ShowEventName = "Local\\AIUsageWidget.Show";

        [STAThread]
        private static void Main(string[] args)
        {
            var startHidden = Array.IndexOf(args, "--hidden") >= 0;
            var screenshotPath = ParseValue(args, "--screenshot=");
            var settingsScreenshotPath = ParseValue(args, "--screenshot-settings=");
            var explicitConfigDirectory = ParseValue(args, "--config-dir=");

            // Screenshot runs are isolated development sessions: they must not be
            // blocked by (or signal) a widget instance that is already running.
            var isolated = screenshotPath != null || settingsScreenshotPath != null;

            var configDirectory = explicitConfigDirectory;
            if (configDirectory == null && isolated)
            {
                // A screenshot must not read real credentials or contact providers
                // unless the caller explicitly supplies a configuration directory.
                configDirectory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "AIUsageWidget-screenshot-" + System.Diagnostics.Process.GetCurrentProcess().Id);

                var screenshotStore = new ConfigStore(configDirectory);
                screenshotStore.Current.OpenCodeEnabled = false;
                screenshotStore.Current.CodexEnabled = false;
                screenshotStore.Current.ClaudeEnabled = false;
                screenshotStore.Current.ShowConsumption = false;
                screenshotStore.Save();
            }
            if (configDirectory == null) configDirectory = ConfigStore.DefaultDirectory;

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created && !isolated)
                {
                    try
                    {
                        using (var signal = EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            signal.Set();
                        }
                    }
                    catch (Exception)
                    {
                        // Another instance without a listener: nothing to do.
                    }
                    return;
                }

                // Initialize logging before configuration load so a load failure
                // is captured.
                Log.Init(configDirectory);
                var store = new ConfigStore(configDirectory);

                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };

                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Error("ui: " + e.Exception.GetType().Name);
                    e.Handled = true;
                };

                var state = new AppState(store);
                var window = new MainWindow(state);
                var tray = new Tray(state, window);

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    var listener = new Thread(() =>
                    {
                        while (true)
                        {
                            showEvent.WaitOne();
                            var dispatcher = app.Dispatcher;
                            if (dispatcher != null)
                            {
                                dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "show-listener"
                    };
                    listener.Start();

                    app.Exit += (s, e) =>
                    {
                        tray.Dispose();
                        state.Dispose();
                    };

                    state.Start();

                    if (!startHidden || screenshotPath != null || settingsScreenshotPath != null)
                    {
                        window.Show();
                        window.Activate();
                    }

                    if (settingsScreenshotPath != null)
                    {
                        var settings = new SettingsWindow(state, window);
                        settings.Show();

                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1.5)
                        };
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop();
                            try
                            {
                                settings.SaveScreenshot(settingsScreenshotPath);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("screenshot-settings: " + ex.GetType().Name);
                            }
                            settings.Close();
                            window.CloseForReal();
                            app.Shutdown();
                        };
                        timer.Start();
                    }
                    else if (screenshotPath != null)
                    {
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(4)
                        };
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop();
                            try
                            {
                                window.SaveScreenshot(screenshotPath);
                            }
                            catch (Exception ex)
                            {
                                Log.Error("screenshot: " + ex.GetType().Name);
                            }
                            window.CloseForReal();
                            app.Shutdown();
                        };
                        timer.Start();
                    }

                    app.Run();
                }
            }
        }

        private static string ParseValue(string[] args, string prefix)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != null && args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = args[i].Substring(prefix.Length).Trim('"');
                    return value.Length == 0 ? null : value;
                }
            }
            return null;
        }
    }
}
