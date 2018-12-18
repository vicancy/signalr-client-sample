using System;

namespace PerformanceTest
{
    internal class ExceptionInfo
    {
        public Type ExceptionType { get; }

        public Exception Exception { get; }

        public int Count { get; private set; }

        public DateTime FirstOccurUtc { get; set; }

        public DateTime LastOccurUtc { get; set; }

        public ExceptionInfo(Exception e)
        {
            Exception = e;
            ExceptionType = e.GetType();
            Count = 1;
            FirstOccurUtc = LastOccurUtc = DateTime.UtcNow;
        }

        public void AddOne()
        {
            Count++;
            LastOccurUtc = DateTime.UtcNow;
        }

        public ExceptionInfo GetMergedExceptionInfo(ExceptionInfo other)
        {
            if (ExceptionType != other.ExceptionType)
            {
                throw new ArgumentException();
            }

            return new ExceptionInfo(Exception)
            {
                Count = Count + other.Count,
                FirstOccurUtc = FirstOccurUtc < other.FirstOccurUtc ? FirstOccurUtc : other.FirstOccurUtc,
                LastOccurUtc = LastOccurUtc > other.LastOccurUtc ? LastOccurUtc : other.LastOccurUtc,
            };
        }
    }
}
