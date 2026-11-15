using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AiUsageWidget
{
    /// <summary>
    /// List price for one model in USD per million tokens. These are catalogue
    /// prices, not a bill: a subscription plan is still charged flat.
    /// </summary>
    public sealed class ModelPrice
    {
        public double Input;
        public double Output;
        public double CacheRead;
        public double CacheWrite;

        private const double PerMillion = 1000000.0;

        /// <summary>
        /// Anthropic reports cache read/write outside input_tokens, so every
        /// bucket is billed separately.
        /// </summary>
        public double CostDisjoint(long input, long output, long cacheRead, long cacheWrite)
        {
            return (input * Input + output * Output + cacheRead * CacheRead + cacheWrite * CacheWrite) / PerMillion;
        }

        /// <summary>
        /// OpenAI reports cached input inside input_tokens and reasoning inside
        /// output_tokens, so only the non-cached remainder is billed at the input
        /// rate.
        /// </summary>
        public double CostInclusiveInput(long input, long output, long cacheRead)
        {
            var fresh = input - cacheRead;
            if (fresh < 0) fresh = 0;

            var cachedRate = CacheRead;
            if (cachedRate <= 0) cachedRate = Input;

            return (fresh * Input + cacheRead * cachedRate + output * Output) / PerMillion;
        }
    }

    public interface IPriceCatalog
    {
        /// <summary>
        /// Best effort refresh. Never throws for network or parse failures, and
        /// never throws for cancellation either: cost is optional data.
        /// </summary>
        void EnsureLoaded(CancellationToken token);

        bool TryGet(string providerId, string model, out ModelPrice price);

        /// <summary>Short provenance for the UI footnote.</summary>
        string Note { get; }
    }

    /// <summary>
    /// Offline catalogue used by tests and as the empty fallback.
    /// </summary>
    public sealed class StaticPriceCatalog : IPriceCatalog
    {
        private readonly Dictionary<string, ModelPrice> _prices =
            new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }

        public void Add(string providerId, string model, ModelPrice price)
        {
            _prices[PriceCatalog.ProviderOf(providerId) + "/" + PriceCatalog.NormalizeModel(model)] = price;
        }

        public void EnsureLoaded(CancellationToken token)
        {
            Calls++;
        }

        public bool TryGet(string providerId, string model, out ModelPrice price)
        {
            price = null;
            return _prices.TryGetValue(
                PriceCatalog.ProviderOf(providerId) + "/" + PriceCatalog.NormalizeModel(model),
                out price);
        }

        public string Note
        {
            get { return "test catalogue"; }
        }
    }

    /// <summary>
    /// Public list prices from models.dev, the same catalogue OpenCode uses to
    /// compute its own cost totals. Only the providers this widget can price are
    /// kept, and the filtered result is cached so the 6 MB document is fetched at
    /// most once a day.
    /// </summary>
    public sealed class PriceCatalog : IPriceCatalog
    {
        public const string SourceUrl = "https://models.dev/api.json";
        private const int FetchTimeoutMs = 20000;
        private const string FileName = "pricing.json";
        private const int CurrentVersion = 1;

        private static readonly string[] PricedProviders = { "anthropic", "openai" };

        private readonly string _directory;
        private readonly string _path;
        private readonly IHttpTransport _transport;
        private readonly IClock _clock;
        private readonly object _gate = new object();

        private Dictionary<string, ModelPrice> _prices;
        private DateTimeOffset _fetchedAt;
        private DateTimeOffset _lastAttempt;
        private bool _attempted;
        private string _note;

        public PriceCatalog(string directory, IHttpTransport transport, IClock clock)
        {
            _directory = directory;
            _path = Path.Combine(directory, FileName);
            _transport = transport ?? HttpTransport.Default;
            _clock = clock ?? SystemClock.Instance;
            _note = "no price source yet";
        }

        public string Note
        {
            get { lock (_gate) { return _note; } }
        }

        public void EnsureLoaded(CancellationToken token)
        {
            lock (_gate)
            {
                if (_prices == null) LoadCacheLocked();

                var now = _clock.UtcNow;
                if (_prices != null && now - _fetchedAt < TimeSpan.FromHours(24)) return;

                // One attempt per day, successful or not: a dead network must not
                // turn every refresh into a six megabyte download.
                if (_attempted && now - _lastAttempt < TimeSpan.FromHours(24)) return;
                _attempted = true;
                _lastAttempt = now;

                RefreshLocked(token);
            }
        }

        public bool TryGet(string providerId, string model, out ModelPrice price)
        {
            price = null;
            if (string.IsNullOrEmpty(model)) return false;

            lock (_gate)
            {
                if (_prices == null) return false;

                var key = ProviderOf(providerId) + "/" + NormalizeModel(model);
                if (_prices.TryGetValue(key, out price)) return true;

                // Anthropic sometimes appends a date to the API model id; the
                // normalized form is the fallback already, so a miss is final.
                price = null;
                return false;
            }
        }

        // -------------------------------------------------------------- internals

        private void RefreshLocked(CancellationToken token)
        {
            try
            {
                var headers = new Dictionary<string, string> { { "Accept", "application/json" } };
                var result = _transport.Get(SourceUrl, headers, FetchTimeoutMs, token, Http.MaxCatalogBytes);
                if (result == null || result.Status != 200 || string.IsNullOrEmpty(result.Body))
                {
                    _note = _prices == null ? "list prices unavailable" : "list prices cached";
                    return;
                }

                var parsed = Parse(result.Body);
                if (parsed.Count == 0)
                {
                    _note = _prices == null ? "list prices unavailable" : "list prices cached";
                    return;
                }

                _prices = parsed;
                _fetchedAt = _clock.UtcNow;
                _note = "models.dev list prices";
                SaveCacheLocked();
            }
            catch (OperationCanceledException)
            {
                // Cost is optional: cancellation must not fail the refresh.
            }
            catch (Exception ex)
            {
                Log.Error("pricing: " + ex.GetType().Name);
                _note = _prices == null ? "list prices unavailable" : "list prices cached";
            }
        }

        internal static Dictionary<string, ModelPrice> Parse(string body)
        {
            var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < PricedProviders.Length; i++)
            {
                var provider = PricedProviders[i];
                var section = Json.SubObject(body, provider);
                if (section == null) continue;

                var root = Json.ParseObject(section);
                var models = Json.Obj(Json.Get(root, "models"));
                if (models == null) continue;

                foreach (var pair in models)
                {
                    var cost = Json.Obj(Json.Get(Json.Obj(pair.Value), "cost"));
                    if (cost == null) continue;

                    var price = new ModelPrice
                    {
                        Input = Json.Num(Json.Get(cost, "input"), -1),
                        Output = Json.Num(Json.Get(cost, "output"), -1),
                        CacheRead = Json.Num(Json.Get(cost, "cache_read"), -1),
                        CacheWrite = Json.Num(Json.Get(cost, "cache_write"), -1)
                    };

                    if (!IsUsable(price)) continue;
                    if (price.CacheRead < 0) price.CacheRead = 0;
                    if (price.CacheWrite < 0) price.CacheWrite = 0;

                    result[provider + "/" + NormalizeModel(pair.Key)] = price;
                }
            }

            return result;
        }

        private static bool IsUsable(ModelPrice price)
        {
            return Json.IsFinite(price.Input) && Json.IsFinite(price.Output) &&
                   price.Input >= 0 && price.Output >= 0 &&
                   (price.Input > 0 || price.Output > 0);
        }

        private void LoadCacheLocked()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var root = Json.ParseObject(File.ReadAllText(_path, Encoding.UTF8));
                if (root == null) return;

                _fetchedAt = Json.Date(Json.Get(root, "FetchedAt")) ?? DateTimeOffset.MinValue;

                var models = Json.Obj(Json.Get(root, "Models"));
                if (models == null) return;

                var loaded = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in models)
                {
                    var value = Json.Obj(pair.Value);
                    if (value == null) continue;

                    var price = new ModelPrice
                    {
                        Input = Json.Num(Json.Get(value, "Input"), -1),
                        Output = Json.Num(Json.Get(value, "Output"), -1),
                        CacheRead = Json.Num(Json.Get(value, "CacheRead"), 0),
                        CacheWrite = Json.Num(Json.Get(value, "CacheWrite"), 0)
                    };

                    if (IsUsable(price)) loaded[pair.Key] = price;
                }

                if (loaded.Count == 0) return;

                _prices = loaded;
                _note = "models.dev list prices (cached)";
            }
            catch (Exception ex)
            {
                Log.Error("pricing cache: " + ex.GetType().Name);
            }
        }

        private void SaveCacheLocked()
        {
            string temp = null;
            try
            {
                Directory.CreateDirectory(_directory);

                var models = new Dictionary<string, object>();
                foreach (var pair in _prices)
                {
                    models[pair.Key] = new Dictionary<string, object>
                    {
                        { "Input", pair.Value.Input },
                        { "Output", pair.Value.Output },
                        { "CacheRead", pair.Value.CacheRead },
                        { "CacheWrite", pair.Value.CacheWrite }
                    };
                }

                var document = new Dictionary<string, object>
                {
                    { "Version", CurrentVersion },
                    { "FetchedAt", _fetchedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
                    { "Models", models }
                };

                temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, Json.Serialize(document), new UTF8Encoding(false));

                if (File.Exists(_path)) File.Replace(temp, _path, null, true);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                Log.Error("pricing save: " + ex.GetType().Name);
                if (temp != null)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------ key mapping

        internal static string ProviderOf(string providerId)
        {
            if (providerId == "claude") return "anthropic";
            if (providerId == "codex") return "openai";
            return providerId;
        }

        internal static string NormalizeModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return string.Empty;

            var value = model.Trim().ToLowerInvariant();

            // OpenCode renders context-window variants as "model[1m]".
            var bracket = value.IndexOf('[');
            if (bracket > 0) value = value.Substring(0, bracket);

            var slash = value.LastIndexOf('/');
            if (slash >= 0 && slash < value.Length - 1) value = value.Substring(slash + 1);

            if (value.EndsWith("-latest", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 7);
            }

            // Anthropic dated aliases: claude-sonnet-4-5-20250929 -> claude-sonnet-4-5
            if (value.Length > 9)
            {
                var suffix = value.Substring(value.Length - 8);
                if (value[value.Length - 9] == '-' && IsDigits(suffix))
                {
                    value = value.Substring(0, value.Length - 9);
                }
            }

            return value;
        }

        private static bool IsDigits(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }
            return true;
        }
    }
}
