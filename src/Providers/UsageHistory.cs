using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AiUsageWidget.Providers
{
    /// <summary>One local date of usage as reported by the OpenCode CLI.</summary>
    public sealed class HistoryDay
    {
        public long Input;
        public long Output;
        public long Reasoning;
        public long CacheRead;
        public long CacheWrite;
        public long Total;
        public double? CostUsd;

        /// <summary>
        /// True when the date is closed: its last sample was taken after midnight,
        /// so the value no longer grows. A day captured live is provisional.
        /// </summary>
        public bool Final;
    }

    /// <summary>
    /// Small persisted store of per-day OpenCode totals. The CLI can only answer
    /// "the last N days", so a day is captured once and then reused, and the
    /// missing days are reconstructed by differencing consecutive windows.
    /// Codex and Claude are not stored here: their per-day series is rebuilt from
    /// the session files on every scan.
    /// </summary>
    public sealed class UsageHistory
    {
        private const string FileName = "usage-history.json";
        private const int CurrentVersion = 1;
        /// <summary>Days kept on disk. The chart renders 30; the rest is headroom.</summary>
        public const int RetentionDays = 400;

        private readonly string _path;
        private readonly Dictionary<string, HistoryDay> _days =
            new Dictionary<string, HistoryDay>(StringComparer.Ordinal);
        private bool _dirty;

        private UsageHistory(string path)
        {
            _path = path;
        }

        public static UsageHistory Load(string directory)
        {
            var path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, FileName);
            var history = new UsageHistory(path);
            history.Read();
            return history;
        }

        public int Count
        {
            get { return _days.Count; }
        }

        public HistoryDay Get(string date)
        {
            HistoryDay day;
            return _days.TryGetValue(date, out day) ? day : null;
        }

        public void Set(string date, HistoryDay day)
        {
            if (string.IsNullOrEmpty(date) || day == null) return;

            HistoryDay existing;
            if (_days.TryGetValue(date, out existing) && Same(existing, day)) return;

            _days[date] = day;
            _dirty = true;
        }

        /// <summary>Drops dates outside the retention window.</summary>
        public void Prune(DateTime oldestLocalDate)
        {
            var cutoff = oldestLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var stale = new List<string>();

            foreach (var pair in _days)
            {
                if (string.CompareOrdinal(pair.Key, cutoff) < 0) stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++) _days.Remove(stale[i]);
            if (stale.Count > 0) _dirty = true;
        }

        public bool Save()
        {
            if (_path == null || !_dirty) return true;

            string temp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                var days = new Dictionary<string, object>();
                foreach (var pair in _days)
                {
                    days[pair.Key] = new Dictionary<string, object>
                    {
                        { "Input", pair.Value.Input },
                        { "Output", pair.Value.Output },
                        { "Reasoning", pair.Value.Reasoning },
                        { "CacheRead", pair.Value.CacheRead },
                        { "CacheWrite", pair.Value.CacheWrite },
                        { "Total", pair.Value.Total },
                        { "Cost", pair.Value.CostUsd.HasValue ? (object)pair.Value.CostUsd.Value : null },
                        { "Final", pair.Value.Final }
                    };
                }

                var document = new Dictionary<string, object>
                {
                    { "Version", CurrentVersion },
                    { "Days", days }
                };

                temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, Json.Serialize(document), new UTF8Encoding(false));

                if (File.Exists(_path)) File.Replace(temp, _path, null, true);
                else File.Move(temp, _path);

                _dirty = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("history: save failed (" + ex.GetType().Name + ")");
                if (temp != null)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch (Exception) { }
                }
                return false;
            }
        }

        private void Read()
        {
            try
            {
                if (_path == null || !File.Exists(_path)) return;

                var root = Json.ParseObject(File.ReadAllText(_path, Encoding.UTF8));
                if (root == null) return;

                var days = Json.Obj(Json.Get(root, "Days"));
                if (days == null) return;

                foreach (var pair in days)
                {
                    var value = Json.Obj(pair.Value);
                    if (value == null) continue;

                    var cost = Json.Get(value, "Cost");
                    _days[pair.Key] = new HistoryDay
                    {
                        Input = Json.Long(Json.Get(value, "Input")),
                        Output = Json.Long(Json.Get(value, "Output")),
                        Reasoning = Json.Long(Json.Get(value, "Reasoning")),
                        CacheRead = Json.Long(Json.Get(value, "CacheRead")),
                        CacheWrite = Json.Long(Json.Get(value, "CacheWrite")),
                        Total = Json.Long(Json.Get(value, "Total")),
                        CostUsd = cost == null ? (double?)null : Json.Num(cost, double.NaN),
                        Final = ReadBool(value, "Final")
                    };

                    if (_days[pair.Key].CostUsd.HasValue && !Json.IsFinite(_days[pair.Key].CostUsd.Value))
                    {
                        _days[pair.Key].CostUsd = null;
                    }
                }

                // Bound the retained window even if the file was edited by hand.
                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("history: load failed (" + ex.GetType().Name + ")");
                _days.Clear();
            }
        }

        private static bool Same(HistoryDay a, HistoryDay b)
        {
            return a.Input == b.Input && a.Output == b.Output && a.Reasoning == b.Reasoning &&
                   a.CacheRead == b.CacheRead && a.CacheWrite == b.CacheWrite &&
                   a.Total == b.Total && a.CostUsd == b.CostUsd && a.Final == b.Final;
        }

        private static bool ReadBool(Dictionary<string, object> obj, string key)
        {
            var value = Json.Get(obj, key);
            if (value is bool) return (bool)value;
            return string.Equals(Json.Str(value), "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
