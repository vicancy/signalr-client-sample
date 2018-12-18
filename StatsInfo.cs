using System;
using System.Collections.Concurrent;

namespace PerformanceTest
{
    internal class StatsInfo
    {
        public int ErrorCount;

        // only used for connect
        public int TotalCount;

        public int NotSentCount;
        public int SuccessCount;

        // only used for send
        public int ReceivedCount;

        public ConcurrentDictionary<Type, ExceptionInfo> Exceptions = new ConcurrentDictionary<Type, ExceptionInfo>();

        public double? MinElapsed;
        public double? MaxElapsed;
        public double? AvgElapsed;

        private double _currentElapsed;
        private readonly object _lock = new object();

        private long _count;
        private double _sum;
        private readonly string _name;
        private readonly string _user;
        private readonly NLog.ILogger _logger;

        public StatsInfo(string name, string user, NLog.ILogger logger)
        {
            _name = name;
            _logger = logger;
            _user = user;
        }
        public double SetElapsed(DateTimeOffset previousTimeStamp)
        {
            lock (_lock)
            {
                _count++;
                var now = DateTimeOffset.Now;
                _currentElapsed = (now - previousTimeStamp).TotalMilliseconds;
                if (MinElapsed == null || MinElapsed > _currentElapsed)
                {
                    MinElapsed = _currentElapsed;
                }

                if (MaxElapsed == null || MaxElapsed < _currentElapsed)
                {
                    MaxElapsed = _currentElapsed;
                }

                _sum +=_currentElapsed;
                AvgElapsed = _sum / _count;

                return _currentElapsed;
            }
        }

        public void AddException(Exception e)
        {
            var count = Exceptions.AddOrUpdate(e.GetType(), s => new ExceptionInfo(e), (s, c) =>
            {
                c.AddOne();
                return c;
            });
        }
    }
}
