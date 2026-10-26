using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AiUsageWidget
{
    /// <summary>
    /// Thin helpers over JavaScriptSerializer (in-box, no NuGet dependency).
    /// All payloads we consume are small JSON objects.
    /// </summary>
    public static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = 1024 * 1024 * 32,
            RecursionLimit = 64
        };

        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return Serializer.DeserializeObject(json) as Dictionary<string, object>;
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
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
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

            if (number <= 0) return null;
            if (number > 1e11) return DateTimeOffset.FromUnixTimeMilliseconds((long)number);
            return DateTimeOffset.FromUnixTimeSeconds((long)number);
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }
    }
}
