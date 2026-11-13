using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Tests
{
    public static class ConfigTests
    {
        private static ConfigStore NewStore(TempDirectory temp)
        {
            return new ConfigStore(temp.Combine("config"));
        }

        [Test]
        public static void Defaults_AreStable()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                var config = store.Current;

                Assert.Equal(AppConfig.CurrentVersion, config.Version);
                Assert.Equal(5, config.QuotaMinutes);
                Assert.Equal(30, config.ConsumptionMinutes);
                Assert.Near(50, config.WarnPercent, 0.001);
                Assert.Near(80, config.CriticalPercent, 0.001);
                Assert.True(config.OpenCodeEnabled);
                Assert.True(config.CodexEnabled);
                Assert.True(config.ClaudeEnabled);
                Assert.Null(config.WindowX);
            }
        }

        [Test]
        public static void Save_RoundTripsThroughDisk()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                store.Current.QuotaMinutes = 9;
                store.Current.WindowX = -1200.5;
                var result = store.Save();

                Assert.True(result.Success);
                Assert.True(File.Exists(store.ConfigPath));

                var reloaded = new ConfigStore(temp.Combine("config"));
                Assert.Equal(9, reloaded.Current.QuotaMinutes);
                Assert.Near(-1200.5, reloaded.Current.WindowX.Value, 0.001);
            }
        }

        [Test]
        public static void SecondSave_CreatesValidBackupOfPreviousDocument()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                store.Current.QuotaMinutes = 7;
                Assert.True(store.Save().Success);

                store.Current.QuotaMinutes = 8;
                Assert.True(store.Save().Success);

                Assert.True(File.Exists(store.BackupPath));
                var backup = Json.ParseObject(File.ReadAllText(store.BackupPath, Encoding.UTF8));
                Assert.NotNull(backup);
                Assert.Equal(7, (int)Json.Num(Json.Get(backup, "QuotaMinutes"), 0));

                var current = Json.ParseObject(File.ReadAllText(store.ConfigPath, Encoding.UTF8));
                Assert.Equal(8, (int)Json.Num(Json.Get(current, "QuotaMinutes"), 0));
            }
        }

        [Test]
        public static void FailedSave_PreservesPreviousFileAndRollsBackMemory()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                store.Current.QuotaMinutes = 7;
                Assert.True(store.Save().Success);
                var original = File.ReadAllText(store.ConfigPath, Encoding.UTF8);

                store.Current.QuotaMinutes = 8;

                SaveResult result;
                using (new FileStream(store.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    result = store.Save();
                }

                Assert.False(result.Success);
                Assert.Equal("save_failed", result.ErrorCode);
                Assert.Equal(original, File.ReadAllText(store.ConfigPath, Encoding.UTF8));
                Assert.Equal(7, store.Current.QuotaMinutes);
                Assert.Equal(0, Directory.GetFiles(store.DirectoryPath, "*.tmp").Length);
            }
        }

        [Test]
        public static void ConcurrentSaves_ProduceACompleteValidDocument()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                var errors = new List<Exception>();
                var tasks = new List<Task>();

                for (int i = 0; i < 8; i++)
                {
                    var value = i + 1;
                    tasks.Add(Task.Run(new Action(() =>
                    {
                        try
                        {
                            store.Current.QuotaMinutes = value;
                            var result = store.Save();
                            if (!result.Success) throw new InvalidOperationException(result.ErrorCode);
                        }
                        catch (Exception ex)
                        {
                            lock (errors) errors.Add(ex);
                        }
                    })));
                }

                Task.WaitAll(tasks.ToArray());

                Assert.Equal(0, errors.Count, errors.Count == 0 ? null : errors[0].Message);
                var parsed = Json.ParseObject(File.ReadAllText(store.ConfigPath, Encoding.UTF8));
                Assert.NotNull(parsed);
                Assert.Equal(0, Directory.GetFiles(store.DirectoryPath, "*.tmp").Length);
            }
        }

        [Test]
        public static void CorruptConfiguration_RecoversFromValidatedBackup()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                store.Current.QuotaMinutes = 7;
                Assert.True(store.Save().Success);
                store.Current.QuotaMinutes = 8;
                Assert.True(store.Save().Success);

                File.WriteAllText(store.ConfigPath, "{not json", Encoding.UTF8);

                var recovered = new ConfigStore(temp.Combine("config"));

                Assert.Equal(7, recovered.Current.QuotaMinutes);
                Assert.Equal(1, Directory.GetFiles(store.DirectoryPath, "*.corrupt-*").Length);
            }
        }

        [Test]
        public static void CorruptConfigurationWithoutBackup_UsesDefaultsAndPreservesEvidence()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                Directory.CreateDirectory(store.DirectoryPath);
                File.WriteAllText(store.ConfigPath, "{not json", Encoding.UTF8);

                var recovered = new ConfigStore(temp.Combine("config"));

                Assert.Equal(5, recovered.Current.QuotaMinutes);
                Assert.Equal(1, Directory.GetFiles(store.DirectoryPath, "*.corrupt-*").Length);
            }
        }

        [Test]
        public static void FutureSchemaVersion_IsNotSilentlyAccepted()
        {
            using (var temp = new TempDirectory())
            {
                var directory = temp.Combine("config");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "config.json"),
                    "{\"Version\":99,\"QuotaMinutes\":9}", Encoding.UTF8);

                var store = new ConfigStore(directory);

                Assert.Equal(AppConfig.CurrentVersion, store.Current.Version);
                Assert.Equal(5, store.Current.QuotaMinutes);
                Assert.Equal(1, Directory.GetFiles(directory, "*.corrupt-*").Length);
            }
        }

        [Test]
        public static void InvalidNumbersAndThresholdOrdering_FallBackToDefaults()
        {
            using (var temp = new TempDirectory())
            {
                var directory = temp.Combine("config");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "config.json"),
                    "{\"QuotaMinutes\":0,\"ConsumptionMinutes\":2,\"WarnPercent\":90," +
                    "\"CriticalPercent\":80,\"WindowX\":\"NaN\",\"WindowY\":1e300}",
                    Encoding.UTF8);

                var store = new ConfigStore(directory);

                Assert.Equal(5, store.Current.QuotaMinutes);
                Assert.Equal(30, store.Current.ConsumptionMinutes);
                Assert.Near(50, store.Current.WarnPercent, 0.001);
                Assert.Near(80, store.Current.CriticalPercent, 0.001);
                Assert.Null(store.Current.WindowX);
                Assert.Null(store.Current.WindowY);
            }
        }

        [Test]
        public static void Dpapi_RoundTripsSyntheticSecret()
        {
            if (!ConfigStore.EncryptionAvailable) return;

            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                var result = store.SetOpenCodeKey("synthetic-test-secret");
                Assert.True(result.Success);

                var reloaded = new ConfigStore(temp.Combine("config"));
                Assert.Equal("synthetic-test-secret", reloaded.GetOpenCodeKey());
                Assert.True(reloaded.HasOpenCodeKey);

                var removed = reloaded.SetOpenCodeKey(null);
                Assert.True(removed.Success);
                Assert.False(reloaded.HasOpenCodeKey);
            }
        }

        [Test]
        public static void InvalidProtectedKey_ReturnsNullWithoutThrowing()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                store.Current.OpenCodeKeyEnc = "not-base64";
                Assert.Null(store.GetOpenCodeKey());
            }
        }

        [Test]
        public static void LoginItemToggle_SuccessPersistsAndAppliesRegistryEntry()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                var autostart = new FakeAutostart();

                var result = LoginItemToggle.Apply(
                    store.Current, store, autostart, true, @"C:\Program Files\AI Usage\AIUsageWidget.exe");

                Assert.True(result.Success);
                Assert.True(store.Current.LaunchAtLogin);
                Assert.True(autostart.Enabled);
                Assert.Contains("AI Usage", autostart.LastPath);

                var reloaded = new ConfigStore(temp.Combine("config"));
                Assert.True(reloaded.Current.LaunchAtLogin);
            }
        }

        [Test]
        public static void LoginItemToggle_AutostartFailureRollsBackConfigAndDisk()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                var autostart = new FakeAutostart { FailSet = true };

                var result = LoginItemToggle.Apply(store.Current, store, autostart, true, "widget.exe");

                Assert.False(result.Success);
                Assert.Equal("autostart_failed", result.ErrorCode);
                Assert.False(store.Current.LaunchAtLogin);

                var reloaded = new ConfigStore(temp.Combine("config"));
                Assert.False(reloaded.Current.LaunchAtLogin);
            }
        }

        [Test]
        public static void ConfigStore_WhenDirectoryPathIsAFile_FailsSaveWithoutThrowing()
        {
            using (var temp = new TempDirectory())
            {
                var file = temp.Write("not-a-directory", "x");
                var store = new ConfigStore(file);
                var result = store.Save();

                Assert.False(result.Success);
                Assert.Equal("save_failed", result.ErrorCode);
                Assert.Equal(5, store.Current.QuotaMinutes);
            }
        }

        [Test]
        public static void ConfigStore_NeverUsesTheRealApplicationDataDirectory()
        {
            using (var temp = new TempDirectory())
            {
                var store = NewStore(temp);
                Assert.True(store.ConfigPath.StartsWith(temp.Path, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    store.ConfigPath);
            }
        }

        [Test]
        public static void Log_WritesToTheConfiguredDirectoryOnly()
        {
            using (var temp = new TempDirectory())
            {
                Log.Init(temp.Path);
                Log.Error("synthetic error code");
                var path = Path.Combine(temp.Path, "widget.log");
                Assert.True(File.Exists(path));
                Assert.Contains("synthetic error code", File.ReadAllText(path));
            }
        }
    }
}
