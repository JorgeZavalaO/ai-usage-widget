using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AiUsageWidget
{
    /// <summary>
    /// Thin helpers over JavaScriptSerializer (in-box, no NuGet dependency).
    /// A fresh serializer is used per operation: the type's instance members are
    /// not documented as thread safe and configuration saves share this class
    /// with background refresh work.
    /// </summary>
    public static class Json
    {
        private const int MaxPayloadChars = 4 * 1024 * 1024;

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = MaxPayloadChars,
                RecursionLimit = 64
            };
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            json = json.TrimStart('\uFEFF');
            if (json.Length == 0) return null;
            try
            {
                return NewSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object[] Arr(object value)
        {
            var arr = value as object[];
            if (arr != null) return arr;
            var list = value as ArrayList;
            return list == null ? null : list.ToArray();
        }

        public static string Str(object value)
        {
            if (value == null) return null;
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double Num(object value, double fallback)
        {
            if (value == null) return fallback;
            try
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) return fallback;
                return number;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>True when the value is a finite number.</summary>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static long Long(object value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static bool Has(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.ContainsKey(key) && obj[key] != null;
        }

        public static object Get(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            object value;
            return obj.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>
        /// Accepts ISO-8601 strings, unix seconds (1.7e9) or unix milliseconds (1.7e12).
        /// Returns null for empty, out-of-range, or unparsable input.
        /// </summary>
        public static DateTimeOffset? Date(object value)
        {
            if (value == null) return null;

            var text = value as string;
            if (text != null)
            {
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out parsed))
                {
                    return parsed;
                }
                return null;
            }

            double number;
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }

            if (!IsFinite(number) || number <= 0) return null;
            if (number > 1e11) return FromUnixMilliseconds(number);
            return FromUnixSeconds(number);
        }

        private static DateTimeOffset? FromUnixSeconds(double value)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DateTimeOffset? FromUnixMilliseconds(double value)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Serialize(object value)
        {
            return NewSerializer().Serialize(value);
        }

        /// <summary>
        /// Returns the balanced JSON object stored under a key, without parsing the
        /// rest of the document. Used to read single providers from a large
        /// catalogue that would otherwise exceed the serializer payload limit.
        /// Returns null when the key is absent or its value is not an object.
        /// </summary>
        public static string SubObject(string json, string key, int maxLength = 12 * 1024 * 1024)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;

            var needle = "\"" + key + "\"";
            var from = 0;
            while (from < json.Length)
            {
                var at = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0) return null;
                from = at + needle.Length;

                var i = from;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != ':') continue;
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != '{') continue;

                var end = FindObjectEnd(json, i, maxLength);
                if (end > 0) return json.Substring(i, end - i);
            }

            return null;
        }

        private static int FindObjectEnd(string json, int start, int maxLength)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < json.Length; i++)
            {
                if (i - start > maxLength) return -1;

                var c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }

            return -1;
        }
    }
}
