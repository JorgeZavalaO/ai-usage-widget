using System;

namespace AiUsageWidget
{
    /// <summary>Narrow time seam so scheduling and aggregation tests never sleep.</summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
        DateTime LocalNow { get; }
        DateTimeOffset ToLocal(DateTimeOffset value);
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTimeOffset UtcNow
        {
            get { return DateTimeOffset.UtcNow; }
        }

        public DateTime LocalNow
        {
            get { return DateTime.Now; }
        }

        public DateTimeOffset ToLocal(DateTimeOffset value)
        {
            return value.ToLocalTime();
        }
    }
}
