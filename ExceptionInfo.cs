using System;

namespace PerformanceTest
{
    internal class ExceptionInfo
    {
        public string FirstOccurUser { get; set; }
        public string LastOccurUser { get; set; }

        public Type ExceptionType { get; }

        public Exception Exception { get; }

        public int Count { get; private set; }

        public DateTime FirstOccurUtc { get; set; }

        public DateTime LastOccurUtc { get; set; }

        public ExceptionInfo(Exception e, string user)
        {
            Exception = e;
            ExceptionType = e.GetType();
            Count = 1;
            FirstOccurUtc = LastOccurUtc = DateTime.UtcNow;
            FirstOccurUser = user;
        }

        public void AddOne(string user)
        {
            Count++;
            LastOccurUtc = DateTime.UtcNow;
            LastOccurUser = user;
        }

        public ExceptionInfo GetMergedExceptionInfo(ExceptionInfo other)
        {
            if (ExceptionType != other.ExceptionType)
            {
                throw new ArgumentException();
            }

            var exp = new ExceptionInfo(Exception, LastOccurUser)
            {
                Count = Count + other.Count
            };

            if (FirstOccurUtc < other.FirstOccurUtc)
            {
                exp.FirstOccurUtc = FirstOccurUtc;
                exp.FirstOccurUser = FirstOccurUser;
            }
            else
            {
                exp.FirstOccurUtc = other.FirstOccurUtc;
                exp.FirstOccurUser = other.FirstOccurUser;
            }

            if (LastOccurUtc > other.LastOccurUtc)
            {
                exp.LastOccurUtc = LastOccurUtc;
                exp.LastOccurUser = LastOccurUser;
            }
            else
            {
                exp.LastOccurUtc = other.LastOccurUtc;
                exp.LastOccurUser = other.LastOccurUser;
            }

            return exp;
        }
    }
}
