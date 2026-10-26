using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageWidget
{
    /// <summary>
    /// Append-only, size-capped log. Never receives tokens or headers:
    /// callers pass provider ids and error codes only.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init(string directory)
        {
            _path = Path.Combine(directory, "widget.log");
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > 256 * 1024)
                    {
                        File.Delete(_path);
                    }
                    File.AppendAllText(
                        _path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never break the widget.
            }
        }
    }

    public sealed class ConfigStore
    {
        private readonly string _directory;
        private readonly string _path;
        private string _plainKey;

        public AppConfig Current { get; private set; }

        public string DirectoryPath
        {
            get { return _directory; }
        }

        public ConfigStore()
        {
            _directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIUsageWidget");
            _path = Path.Combine(_directory, "config.json");
            System.IO.Directory.CreateDirectory(_directory);
            Current = Load();
        }

        public static bool EncryptionAvailable
        {
            get
            {
                try
                {
                    var probe = ProtectedData.Protect(new byte[] { 1 }, null, DataProtectionScope.CurrentUser);
                    return probe != null && probe.Length > 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string GetOpenCodeKey()
        {
            if (_plainKey != null) return _plainKey;
            if (string.IsNullOrEmpty(Current.OpenCodeKeyEnc)) return null;

            try
            {
                var bytes = Convert.FromBase64String(Current.OpenCodeKeyEnc);
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                _plainKey = Encoding.UTF8.GetString(plain);
                return _plainKey;
            }
            catch (Exception ex)
            {
                Log.Error("config: cannot decrypt stored key (" + ex.GetType().Name + ")");
                return null;
            }
        }

        public void SetOpenCodeKey(string key)
        {
            _plainKey = null;
            if (string.IsNullOrEmpty(key))
            {
                Current.OpenCodeKeyEnc = null;
                Save();
                return;
            }

            if (!EncryptionAvailable) return;

            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            Current.OpenCodeKeyEnc = Convert.ToBase64String(bytes);
            _plainKey = key.Trim();
            Save();
        }

        public bool HasOpenCodeKey
        {
            get { return !string.IsNullOrEmpty(Current.OpenCodeKeyEnc); }
        }

        public void Save()
        {
            try
            {
                var json = Json.Serialize(Current);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                Log.Error("config: save failed (" + ex.GetType().Name + ")");
            }
        }

        private AppConfig Load()
        {
            try
            {
                if (!File.Exists(_path)) return AppConfig.CreateDefault();
                var json = File.ReadAllText(_path, Encoding.UTF8);
                var parsed = Json.ParseObject(json);
                if (parsed == null) return AppConfig.CreateDefault();

                var config = new AppConfig();
                var defaults = AppConfig.CreateDefault();
                config.Version = defaults.Version;
                config.AlwaysOnTop = ReadBool(parsed, "AlwaysOnTop", defaults.AlwaysOnTop);
                config.LaunchAtLogin = ReadBool(parsed, "LaunchAtLogin", defaults.LaunchAtLogin);
                config.Collapsed = ReadBool(parsed, "Collapsed", defaults.Collapsed);
                config.ShowConsumption = ReadBool(parsed, "ShowConsumption", defaults.ShowConsumption);
                config.OpenCodeEnabled = ReadBool(parsed, "OpenCodeEnabled", defaults.OpenCodeEnabled);
                config.CodexEnabled = ReadBool(parsed, "CodexEnabled", defaults.CodexEnabled);
                config.ClaudeEnabled = ReadBool(parsed, "ClaudeEnabled", defaults.ClaudeEnabled);
                config.OpenCodeKeyEnc = Json.Str(Json.Get(parsed, "OpenCodeKeyEnc"));
                config.QuotaMinutes = (int)Json.Num(Json.Get(parsed, "QuotaMinutes"), defaults.QuotaMinutes);
                config.ConsumptionMinutes = (int)Json.Num(Json.Get(parsed, "ConsumptionMinutes"), defaults.ConsumptionMinutes);
                config.WarnPercent = Json.Num(Json.Get(parsed, "WarnPercent"), defaults.WarnPercent);
                config.CriticalPercent = Json.Num(Json.Get(parsed, "CriticalPercent"), defaults.CriticalPercent);

                var wx = Json.Get(parsed, "WindowX");
                var wy = Json.Get(parsed, "WindowY");
                if (wx != null) config.WindowX = Json.Num(wx, 0);
                if (wy != null) config.WindowY = Json.Num(wy, 0);

                if (config.QuotaMinutes < 1) config.QuotaMinutes = defaults.QuotaMinutes;
                if (config.ConsumptionMinutes < 5) config.ConsumptionMinutes = defaults.ConsumptionMinutes;
                return config;
            }
            catch (Exception ex)
            {
                Log.Error("config: load failed (" + ex.GetType().Name + ")");
                return AppConfig.CreateDefault();
            }
        }

        private static bool ReadBool(System.Collections.Generic.Dictionary<string, object> obj, string key, bool fallback)
        {
            var value = Json.Get(obj, key);
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            var text = Json.Str(value);
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AIUsageWidget";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Set(bool enabled, string executablePath)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        key.SetValue(ValueName, "\"" + executablePath + "\" --hidden");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("autostart: " + ex.GetType().Name);
            }
        }
    }
}
