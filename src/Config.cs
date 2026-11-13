using System;
using System.Collections.Generic;
using System.Globalization;
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
            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "widget.log");
            }
            catch (Exception)
            {
                _path = null;
            }
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

    public sealed class SaveResult
    {
        public bool Success;
        public string ErrorCode;

        public static SaveResult Ok()
        {
            return new SaveResult { Success = true };
        }

        public static SaveResult Fail(string code)
        {
            return new SaveResult { Success = false, ErrorCode = code };
        }
    }

    public sealed class ConfigStore
    {
        private const string FileName = "config.json";
        private const string BackupName = "config.json.bak";

        private readonly string _directory;
        private readonly string _path;
        private readonly string _backupPath;
        private readonly object _saveGate = new object();
        private string _plainKey;
        private AppConfig _committed;

        public AppConfig Current { get; private set; }
        public string LastError { get; private set; }

        public string DirectoryPath
        {
            get { return _directory; }
        }

        public string ConfigPath
        {
            get { return _path; }
        }

        public string BackupPath
        {
            get { return _backupPath; }
        }

        public static string DefaultDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIUsageWidget");
            }
        }

        public ConfigStore()
            : this(DefaultDirectory)
        {
        }

        public ConfigStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) directory = DefaultDirectory;
            _directory = directory;
            _path = Path.Combine(_directory, FileName);
            _backupPath = Path.Combine(_directory, BackupName);
            _committed = AppConfig.CreateDefault();
            Current = _committed;

            try
            {
                System.IO.Directory.CreateDirectory(_directory);
            }
            catch (Exception ex)
            {
                Log.Error("config: cannot create directory (" + ex.GetType().Name + ")");
            }

            Current = Load();
            _committed = Current.Clone();
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

        public SaveResult SetOpenCodeKey(string key)
        {
            _plainKey = null;
            if (string.IsNullOrEmpty(key))
            {
                Current.OpenCodeKeyEnc = null;
                return Save();
            }

            if (!EncryptionAvailable) return SaveResult.Fail("dpapi_unavailable");

            try
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(key.Trim()),
                    null,
                    DataProtectionScope.CurrentUser);
                Current.OpenCodeKeyEnc = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                Log.Error("config: cannot encrypt key (" + ex.GetType().Name + ")");
                return SaveResult.Fail("dpapi_failed");
            }

            var result = Save();
            if (result.Success) _plainKey = key.Trim();
            return result;
        }

        public bool HasOpenCodeKey
        {
            get { return !string.IsNullOrEmpty(Current.OpenCodeKeyEnc); }
        }

        /// <summary>
        /// Persists the current configuration. The previous valid document is kept
        /// as config.json.bak and replacement is atomic on the same volume. On
        /// failure the in-memory configuration is rolled back so callers cannot
        /// report success while disk state differs.
        /// </summary>
        public SaveResult Save()
        {
            lock (_saveGate)
            {
                string temp = null;
                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    var json = Json.Serialize(Current);
                    temp = Path.Combine(
                        _directory,
                        FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

                    File.WriteAllText(temp, json, new UTF8Encoding(false));

                    if (File.Exists(_path))
                    {
                        File.Replace(temp, _path, _backupPath, true);
                    }
                    else
                    {
                        File.Move(temp, _path);
                    }

                    _committed = Current.Clone();
                    LastError = null;
                    return SaveResult.Ok();
                }
                catch (Exception ex)
                {
                    if (temp != null)
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); }
                        catch (Exception) { }
                    }

                    Current = _committed.Clone();
                    _plainKey = null;
                    LastError = ex.GetType().Name;
                    Log.Error("config: save failed (" + ex.GetType().Name + ")");
                    return SaveResult.Fail("save_failed");
                }
            }
        }

        private AppConfig Load()
        {
            var defaults = AppConfig.CreateDefault();

            try
            {
                if (!File.Exists(_path)) return defaults;

                var parsed = ReadConfig(_path);
                if (parsed == null || ReadVersion(parsed) > AppConfig.CurrentVersion)
                {
                    Log.Error("config: unreadable or unsupported schema");
                    PreserveCorruptFile();
                    return LoadBackup(defaults);
                }

                return FromDictionary(parsed, defaults);
            }
            catch (Exception ex)
            {
                Log.Error("config: load failed (" + ex.GetType().Name + ")");
                return defaults;
            }
        }

        private AppConfig LoadBackup(AppConfig defaults)
        {
            try
            {
                if (!File.Exists(_backupPath)) return defaults;

                var parsed = ReadConfig(_backupPath);
                if (parsed == null || ReadVersion(parsed) > AppConfig.CurrentVersion) return defaults;

                Log.Info("config: recovered from backup");
                return FromDictionary(parsed, defaults);
            }
            catch (Exception)
            {
                return defaults;
            }
        }

        private static Dictionary<string, object> ReadConfig(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return Json.ParseObject(text);
        }

        private static int ReadVersion(Dictionary<string, object> parsed)
        {
            return (int)Json.Num(Json.Get(parsed, "Version"), AppConfig.CurrentVersion);
        }

        private void PreserveCorruptFile()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var target = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                File.Move(_path, target);
                Log.Info("config: corrupt file preserved as .corrupt");
            }
            catch (Exception ex)
            {
                Log.Error("config: cannot preserve corrupt file (" + ex.GetType().Name + ")");
            }
        }

        private static AppConfig FromDictionary(Dictionary<string, object> parsed, AppConfig defaults)
        {
            var config = new AppConfig
            {
                Version = AppConfig.CurrentVersion,
                AlwaysOnTop = ReadBool(parsed, "AlwaysOnTop", defaults.AlwaysOnTop),
                LaunchAtLogin = ReadBool(parsed, "LaunchAtLogin", defaults.LaunchAtLogin),
                Collapsed = ReadBool(parsed, "Collapsed", defaults.Collapsed),
                ShowConsumption = ReadBool(parsed, "ShowConsumption", defaults.ShowConsumption),
                OpenCodeEnabled = ReadBool(parsed, "OpenCodeEnabled", defaults.OpenCodeEnabled),
                CodexEnabled = ReadBool(parsed, "CodexEnabled", defaults.CodexEnabled),
                ClaudeEnabled = ReadBool(parsed, "ClaudeEnabled", defaults.ClaudeEnabled),
                OpenCodeKeyEnc = Json.Str(Json.Get(parsed, "OpenCodeKeyEnc")),
                QuotaMinutes = (int)Json.Num(Json.Get(parsed, "QuotaMinutes"), defaults.QuotaMinutes),
                ConsumptionMinutes = (int)Json.Num(Json.Get(parsed, "ConsumptionMinutes"), defaults.ConsumptionMinutes),
                WarnPercent = Json.Num(Json.Get(parsed, "WarnPercent"), defaults.WarnPercent),
                CriticalPercent = Json.Num(Json.Get(parsed, "CriticalPercent"), defaults.CriticalPercent)
            };

            var wx = Json.Get(parsed, "WindowX");
            var wy = Json.Get(parsed, "WindowY");
            if (wx != null) config.WindowX = Json.Num(wx, double.NaN);
            if (wy != null) config.WindowY = Json.Num(wy, double.NaN);

            return Validate(config, defaults);
        }

        private static AppConfig Validate(AppConfig config, AppConfig defaults)
        {
            config.Version = AppConfig.CurrentVersion;

            if (config.QuotaMinutes < 1 || config.QuotaMinutes > 1440)
            {
                config.QuotaMinutes = defaults.QuotaMinutes;
            }
            if (config.ConsumptionMinutes < 5 || config.ConsumptionMinutes > 1440)
            {
                config.ConsumptionMinutes = defaults.ConsumptionMinutes;
            }

            if (!Json.IsFinite(config.WarnPercent) || config.WarnPercent < 0 || config.WarnPercent > 100)
            {
                config.WarnPercent = defaults.WarnPercent;
            }
            if (!Json.IsFinite(config.CriticalPercent) || config.CriticalPercent < 0 || config.CriticalPercent > 100)
            {
                config.CriticalPercent = defaults.CriticalPercent;
            }
            if (config.WarnPercent >= config.CriticalPercent)
            {
                config.WarnPercent = defaults.WarnPercent;
                config.CriticalPercent = defaults.CriticalPercent;
            }

            if (config.WindowX.HasValue &&
                (!Json.IsFinite(config.WindowX.Value) || Math.Abs(config.WindowX.Value) > 1000000))
            {
                config.WindowX = null;
            }
            if (config.WindowY.HasValue &&
                (!Json.IsFinite(config.WindowY.Value) || Math.Abs(config.WindowY.Value) > 1000000))
            {
                config.WindowY = null;
            }

            return config;
        }

        private static bool ReadBool(Dictionary<string, object> obj, string key, bool fallback)
        {
            var value = Json.Get(obj, key);
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            var text = Json.Str(value);
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public interface IAutostart
    {
        bool IsEnabled();
        bool IsEnabledFor(string executablePath);
        bool Set(bool enabled, string executablePath);
    }

    public sealed class RegistryAutostart : IAutostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AIUsageWidget";

        public bool IsEnabled()
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

        public bool IsEnabledFor(string executablePath)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(executablePath)) return false;

                    var expected = "\"" + executablePath + "\" --hidden";
                    return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Set(bool enabled, string executablePath)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        if (string.IsNullOrEmpty(executablePath)) return false;
                        key.SetValue(ValueName, "\"" + executablePath + "\" --hidden");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("autostart: " + ex.GetType().Name);
                return false;
            }
        }
    }

    public sealed class LoginItemResult
    {
        public bool Success;
        public string ErrorCode;
    }

    /// <summary>
    /// Applies the launch-at-login toggle as one unit: config persistence first,
    /// then the registry. A registry failure rolls the setting back so the UI
    /// cannot claim a change that did not happen.
    /// </summary>
    public static class LoginItemToggle
    {
        public static LoginItemResult Apply(
            AppConfig config,
            ConfigStore store,
            IAutostart autostart,
            bool enabled,
            string executablePath)
        {
            var previous = config.LaunchAtLogin;
            config.LaunchAtLogin = enabled;

            var saved = store.Save();
            if (!saved.Success)
            {
                return new LoginItemResult { Success = false, ErrorCode = saved.ErrorCode };
            }

            if (!autostart.Set(enabled, executablePath))
            {
                config.LaunchAtLogin = previous;
                var rollback = store.Save();
                if (!rollback.Success)
                {
                    Log.Error("autostart: rollback save failed (" + rollback.ErrorCode + ")");
                }
                return new LoginItemResult { Success = false, ErrorCode = "autostart_failed" };
            }

            return new LoginItemResult { Success = true };
        }
    }
}
