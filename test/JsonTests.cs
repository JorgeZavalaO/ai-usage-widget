using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Tests
{
    public static class JsonTests
    {
        [Test]
        public static void ParseObject_RejectsNullAndMalformedInput()
        {
            Assert.Null(Json.ParseObject(null));
            Assert.Null(Json.ParseObject(string.Empty));
            Assert.Null(Json.ParseObject("   "));
            Assert.Null(Json.ParseObject("{"));
            Assert.Null(Json.ParseObject("not json"));
            Assert.Null(Json.ParseObject("[1,2,3]"));
            Assert.Null(Json.ParseObject("{\"a\":1} trailing"));
        }

        [Test]
        public static void ParseObject_ReadsNestedValues()
        {
            var root = Json.ParseObject("{\"a\":{\"b\":42},\"list\":[1,2],\"text\":\"x\"}");
            Assert.NotNull(root);
            Assert.Equal(42L, Json.Long(Json.Get(Json.Obj(Json.Get(root, "a")), "b")));
            Assert.Equal(2, Json.Arr(Json.Get(root, "list")).Length);
            Assert.Equal("x", Json.Str(Json.Get(root, "text")));
        }

        [Test]
        public static void ParseObject_ToleratesUtf8Bom()
        {
            var root = Json.ParseObject("\uFEFF{\"a\":1}");
            Assert.NotNull(root);
            Assert.Equal(1L, Json.Long(Json.Get(root, "a")));
        }

        [Test]
        public static void Num_RejectsNonFiniteAndUnparsableValues()
        {
            Assert.Equal(12.5, Json.Num("12.5", -1));
            Assert.Equal(12.5, Json.Num(12.5, -1));
            Assert.Equal(-1, Json.Num("NaN", -1));
            Assert.Equal(-1, Json.Num("Infinity", -1));
            Assert.Equal(-1, Json.Num("-Infinity", -1));
            Assert.Equal(-1, Json.Num(null, -1));
            Assert.Equal(-1, Json.Num("abc", -1));
            Assert.False(Json.IsFinite(double.NaN));
            Assert.False(Json.IsFinite(double.PositiveInfinity));
            Assert.True(Json.IsFinite(0));
        }

        [Test]
        public static void Long_RejectsUnparsableValues()
        {
            Assert.Equal(42L, Json.Long("42"));
            Assert.Equal(-3L, Json.Long(-3));
            Assert.Equal(0L, Json.Long(null));
            Assert.Equal(0L, Json.Long("42.9"));
            Assert.Equal(0L, Json.Long("nope"));
        }

        [Test]
        public static void Date_AcceptsIsoSecondsAndMilliseconds()
        {
            var iso = Json.Date("2026-11-13T12:00:00Z");
            Assert.NotNull(iso);
            Assert.Equal(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), iso.Value);

            var seconds = Json.Date(1794589200L);
            Assert.NotNull(seconds);
            Assert.Equal(1794589200L, seconds.Value.ToUnixTimeSeconds());

            var milliseconds = Json.Date(1794589200000L);
            Assert.NotNull(milliseconds);
            Assert.Equal(1794589200L, milliseconds.Value.ToUnixTimeSeconds());
        }

        [Test]
        public static void Date_RejectsInvalidOrOutOfRangeInput()
        {
            Assert.Null(Json.Date(null));
            Assert.Null(Json.Date("not a date"));
            Assert.Null(Json.Date(0));
            Assert.Null(Json.Date(-5));
            Assert.Null(Json.Date(1e30));
            Assert.Null(Json.Date(double.NaN));
            Assert.Null(Json.Date("9999-99-99T99:99:99Z"));
        }

        [Test]
        public static void Serialize_RoundTripsConfigurationWithoutSharedState()
        {
            var config = AppConfig.CreateDefault();
            config.QuotaMinutes = 9;
            config.WindowX = -1200.5;
            config.OpenCodeKeyEnc = null;

            var json = Json.Serialize(config);
            var parsed = Json.ParseObject(json);

            Assert.NotNull(parsed);
            Assert.Equal(9, (int)Json.Num(Json.Get(parsed, "QuotaMinutes"), 0));
            Assert.Near(-1200.5, Json.Num(Json.Get(parsed, "WindowX"), 0), 0.001);
        }

        [Test]
        public static void Serializer_IsSafeAcrossConcurrentCallers()
        {
            var errors = new List<Exception>();
            var tasks = new List<Task>();

            for (int i = 0; i < 8; i++)
            {
                tasks.Add(Task.Run(new Action(() =>
                {
                    try
                    {
                        for (int iteration = 0; iteration < 250; iteration++)
                        {
                            var json = Json.Serialize(AppConfig.CreateDefault());
                            var parsed = Json.ParseObject(json);
                            if (parsed == null) throw new InvalidOperationException("Parse returned null.");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex);
                    }
                })));
            }

            Task.WaitAll(tasks.ToArray());
            Assert.Equal(0, errors.Count, errors.Count == 0 ? null : errors[0].GetType().Name);
        }
    }
}
