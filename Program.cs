using CommandLine;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceTest
{
    class Program
    {
        static readonly string host = "https://signalrgameserver20181211040325.azurewebsites.net";
        //static readonly string host = "http://localhost:5000";

        public class Options
        {
            [Option('t', "ThreadCount", Required = false, HelpText = "并发线程数")]
            public int ThreadCount { set; get; } = 1;

            [Option('d', "DelayMilliseconds", Required = false, HelpText = "消息间隔时间，单位毫秒，最小10")]
            public int DelayMilliseconds { set; get; } = 5000;

            [Option('m', "MessageCount", Required = false, HelpText = "每个线程测试的消息数，小于或等于0表示不限")]
            public int MessageCount { set; get; } = 0;

            [Option('u', "Url", Required = false, HelpText = "服务器地址")]
            public string Url { set; get; } = host;

            [Option('s', "Size", Required = false, Default = 100, HelpText = "消息字节数，最大10000，最小1")]
            public int Size { set; get; } = 100;

            [Option('a', "AvgCount", Required = false, Default = 100, HelpText = "统计平均值的数目，最大1000，最小10")]
            public int AvgCount { set; get; } = 100;

            [Option('U', "UserIdPrfix", Required = false, Default = "", HelpText = "用户id前缀,最长40")]
            public string UserIdPrfix { set; get; } = "";
        }

        static void Main(string[] args)
        {
            ThreadPool.SetMinThreads(16, 16);
            NLog.Logger logger = NLog.LogManager.GetLogger("Perf");

            //if (args.Length > 0 && args[args.Length - 1].StartsWith("&"))
            //{
            //    Array.Resize(ref args, args.Length - 1);
            //}
            Console.OutputEncoding = Encoding.UTF8;
            var testers = new ConcurrentBag<Tester>();
            bool stop = false;

            CommandLine.Parser.Default.ParseArguments<Options>(args)
                .WithParsed<Options>(options =>
                {
                    Random random = new Random();

                    if (options.ThreadCount < 1)
                    {
                        logger.Error("Wrong threads");
                        return;
                    }
                    else if (options.Size > 10000 || options.Size < 1)
                    {
                        logger.Error("Wrong size");
                        return;
                    }
                    else if (options.DelayMilliseconds < 10)
                    {
                        logger.Error("Wrong interval");
                        return;
                    }
                    else if (string.IsNullOrWhiteSpace(options.Url) || !options.Url.StartsWith("http"))
                    {
                        logger.Error("Wrong server url");
                        return;
                    }
                    else if (options.AvgCount < 10 || options.AvgCount > 1000)
                    {
                        logger.Error("Wrong AvgCount");
                        return;
                    }
                    else if (options.UserIdPrfix.Length > 40)
                    {
                        logger.Error("Wrong UserIdPrefix");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(options.UserIdPrfix))
                    {
                        options.UserIdPrfix = "user-" + random.Next(1000, 9999);
                    }

                    var concurrent = 32;
                    var slim = new SemaphoreSlim(concurrent);
                    Task.Run(async () =>
                    {
                        logger.Info("Init...");
                        var now = DateTime.Now;
                        byte[] content = new byte[options.Size];
                        var initTask = Task.WhenAll(Enumerable.Range(0, options.ThreadCount).Select(async i =>
                        {
                            await slim.WaitAsync();
                            try
                            {
                                var tester = new Tester(options.Url, options.UserIdPrfix + "_" + i);
                                testers.Add(tester);
                                await tester.Connect();
                            }
                            finally
                            {
                                slim.Release();
                            }
                        }));

                        _ = GetStatsTask(testers, options.ThreadCount, logger, now);

                        await initTask;

                    }).ContinueWith(context =>
                    {
                        byte[] content = new byte[options.Size];
                        while (!stop)
                        {
                            DateTimeOffset start = DateTimeOffset.UtcNow;
                            foreach (Tester tester in testers)
                            {
                                random.NextBytes(content);
                                _ = tester.RunTest(content);
                            }

                            TimeSpan delay = TimeSpan.FromMilliseconds(options.DelayMilliseconds) - (DateTimeOffset.UtcNow - start);
                            if (delay.TotalMilliseconds > 0)
                            {
                                Thread.Sleep(delay);
                            }

                        }
                    }
                    );
                }).WithNotParsed<Options>(error =>
                        {
                            logger.Error("Wrong options");
                        });
            Console.Read();
        }

        private static async Task GetStatsTask(ConcurrentBag<Tester> testers, int count, NLog.Logger logger, DateTime startTime)
        {
            double? maxRate = null;
            double? minRate = null;
            // initializing:
            while (true)
            {
                await Task.Delay(2000);
                var now = DateTime.Now;
                var currentCount = testers.Count;
                var elapsed = (now - startTime).TotalMilliseconds / 1000;
                var currentRate = currentCount / elapsed;
                if (maxRate == null || maxRate < currentRate)
                {
                    maxRate = currentRate;
                }

                if (minRate == null || minRate > currentRate)
                {
                    minRate = currentRate;
                }

                var sb = new StringBuilder();
                sb.Append($"Starts {currentCount}/{count}, current {currentRate:.00}/s, max {maxRate:.00}/s, min {minRate:.00}/s; " +
                    $"Connect Elapsed: max {testers.Max(s => s.ConnectStats.MaxElapsed):.00}, min {testers.Min(s => s.ConnectStats.MinElapsed):.00}, avg {testers.Average(s => s.ConnectStats.AvgElapsed):.00}");

                sb = LogExceptions(testers, sb);
                logger.Info(sb);
                if (currentCount == count)
                {
                    break;
                }
            }

            // running:
            // aggregate exception count
            while (true)
            {
                await Task.Delay(2000);
                var sb = new StringBuilder();
                sb.Append($"\n\t[connected]{testers.Count(s => s.ConnectStatus == Status.Connected)}/[connecting]{testers.Count(s => s.ConnectStatus == Status.Connecting)}/[disconnected]{testers.Count(s => s.ConnectStatus == Status.Disconnected)}/[total]{count};" +
                    $"\n\t[send]{testers.Sum(s => s.SendMessageStats.ReceivedCount)} messages," +
                    $" avg {testers.Average(s => s.SendMessageStats.AvgElapsed)}ms, " +
                    $" max {testers.Max(s => s.SendMessageStats.MaxElapsed)}ms, min {testers.Min(s => s.SendMessageStats.MinElapsed)}ms " +
                    $"\n\t[send]max success {testers.Max(s => s.SendMessageStats.SuccessCount)};  max not sent {testers.Max(s => s.SendMessageStats.NotSentCount)}; max error {testers.Max(s => s.SendMessageStats.ErrorCount)};" +
                    $" max not received {testers.Max(s => s.SendMessageStats.SuccessCount - s.SendMessageStats.ReceivedCount)}");

                sb = LogExceptions(testers, sb);
                logger.Info(sb);
            }
        }

        private static StringBuilder LogExceptions(ConcurrentBag<Tester> testers, StringBuilder sb)
        {
            var dictionary = new Dictionary<Type, ExceptionInfo>();
            foreach (var i in testers.SelectMany(s => s.ConnectStats.Exceptions.Concat(s.RecoverStats.Exceptions)))
            {
                if (dictionary.TryGetValue(i.Key, out var val))
                {
                    dictionary[i.Key] = val.GetMergedExceptionInfo(i.Value);
                }
                else
                {
                    dictionary[i.Key] = i.Value;
                }
            }

            if (dictionary.Count > 0)
            {
                sb.Append($"\n\t[retry] max retry {testers.Max(s => s.RecoverStats.ErrorCount + 1)}, avg {testers.Average(s => s.RecoverStats.ErrorCount + 1)}");
                sb.Append($"\n\t[reconnect] max {testers.Max(s => s.RecoverStats.MaxElapsed)}ms, avg {testers.Average(s => s.RecoverStats.AvgElapsed)}ms, min {testers.Min(s => s.RecoverStats.MinElapsed)}ms ");
                sb.Append($"\n\t[EXCEPTIONS]\n\t|\tFirst\t|\tLast\t|\tCount\t|\tDetail\t");
                foreach (var i in dictionary)
                {
                    sb.Append($"\n\t|{i.Value.FirstOccurUtc}({i.Value.FirstOccurUser})|{i.Value.LastOccurUtc}({i.Value.LastOccurUser})|{i.Value.Count}|{i.Value.Exception}");
                }
            }

            return sb;
        }
    }
}
