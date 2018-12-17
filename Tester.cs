using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace PerformanceTest
{
    internal class StatsInfo
    {
        public int ErrorCount;
        public int TotalCount;
        public int NotSentCount;
        public int SuccessCount;
        public int ReceivedCount;

        public ConcurrentDictionary<Type, (Exception,int)> Exceptions = new ConcurrentDictionary<Type, (Exception, int)>();

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
            var count = Exceptions.AddOrUpdate(e.GetType(), (e, 1), (s, c) =>
            {
                c.Item2 += 1;
                return c;
            });
        }
    }

    class Tester
    {
        private static readonly HttpClient _client = new HttpClient();

        protected static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private HubConnection connection;

        public bool IsConnected { get; protected set; } = false;

        public bool IsConnecting { get; protected set; } = false;

        public StatsInfo ConnectStats { get; private set; }

        public StatsInfo RecoverStats { get; private set; }

        public StatsInfo SendMessageStats { get; private set; }

        private readonly string host;

        private AccessInfo accessInfo;

        public readonly string userId;


        private DateTimeOffset lastConnectTime;

        private DateTimeOffset _lastAuthTime;

        public Tester(string host, string userId = null)
        {
            this.host = host;
            if (string.IsNullOrWhiteSpace(userId))
            {
                this.userId = Guid.NewGuid().ToString();
            }
            else
            {
                this.userId = userId;
            }
            ConnectStats = new StatsInfo("connect", userId, logger);
            RecoverStats = new StatsInfo("recover", userId, logger);
            SendMessageStats = new StatsInfo("echo", userId, logger);
        }

        public async Task Connect()
        {

            if (IsConnected || IsConnecting)
            {
                return;
            }
            IsConnecting = true;

            try
            {
                await AuthAndConnectCore();
            }
            catch (Exception e)
            {
                await RetryConnect(e);
            }
        }

        private async Task AuthAndConnectCore(bool retry = false)
        {
            _lastAuthTime = DateTime.Now;
            (string url, string accessToken) = await GetAuth();

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(accessToken))
            {
                throw new ArgumentNullException("url or accesstoken is null");
            }

            if (retry)
            {
                await connection.DisposeAsync();
            }

            connection = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(accessToken);
                    options.CloseTimeout = TimeSpan.FromSeconds(15);
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                })
                //.AddMessagePackProtocol()
                .Build();

            connection.Closed += ex =>
            {
                IsConnected = false;
                IsConnecting = false;
                return RetryConnect(ex);
            };

            connection.On("Connected", () =>
            {
                Interlocked.Increment(ref ConnectStats.SuccessCount);

                var elapsed = ConnectStats.SetElapsed(lastConnectTime);

                IsConnected = true;
                IsConnecting = false;
            });

            connection.On<MessageBody>("PerformanceTest", message =>
            {
                Interlocked.Increment(ref SendMessageStats.ReceivedCount);
                SendMessageStats.SetElapsed(message.CreatedTime);
            });

            await ConnectCore();
        }

        private async Task RetryConnect(Exception ex)
        {
            if (ex != null)
            {
                ConnectStats.AddException(ex);

                Interlocked.Increment(ref ConnectStats.ErrorCount);
            }

            var reconnectNow = DateTime.Now;
            while (true)
            {
                try
                {
                    // delay a random value < 20s
                    await Task.Delay(new Random((int)Stopwatch.GetTimestamp()).Next(300, 20000));
                    if (DateTime.Now > _lastAuthTime.AddMinutes(50))
                    {
                        // Reauth
                        await AuthAndConnectCore(true);
                    }
                    else
                    {
                        // direct connect
                        await ConnectCore();
                    }

                    RecoverStats.SetElapsed(reconnectNow);
                    Interlocked.Increment(ref RecoverStats.SuccessCount);
                    break;
                }
                catch (Exception e)
                {
                    RecoverStats.AddException(e);

                    Interlocked.Increment(ref RecoverStats.ErrorCount);
                    Interlocked.Increment(ref ConnectStats.ErrorCount);
                }
            }
        }

        private async Task ConnectCore()
        {
            lastConnectTime = DateTime.Now;
            Interlocked.Increment(ref ConnectStats.TotalCount);
            await connection.StartAsync();
        }

        private async Task<(string, string)> GetAuth()
        {
            if (accessInfo == null || accessInfo.TokenExpire < DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds())
            {
                string result = await _client.GetStringAsync($"{host}/auth/login/?username={userId}&password=admin");

                if (string.IsNullOrEmpty(result))
                {
                    logger.Error($"用户 {userId}登录失败");
                    return (null, null);
                }

                try
                {
                    accessInfo = JsonConvert.DeserializeObject<AccessInfo>(result);
                    if (!string.IsNullOrEmpty(accessInfo.Error))
                    {
                        logger.Error($"用户 {userId} 登录失败," + accessInfo.Error);
                        accessInfo = null;
                        return (null, null);
                    }
                    return (accessInfo.SignalRUrl, accessInfo.SignalRToken);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"用户 {userId} 登录失败");
                    return (null, null);
                }
            }
            return (accessInfo.SignalRUrl, accessInfo.SignalRToken);
        }

        public async Task RunTest(byte[] content)
        {
            try
            {
                if (IsConnected)
                {
                    await connection.SendAsync("PerformanceTest", new MessageBody
                    {
                        CreatedTime = DateTimeOffset.Now,
                        Contnet = content
                    });

                    Interlocked.Increment(ref SendMessageStats.SuccessCount);
                }
                else
                {
                    Interlocked.Increment(ref SendMessageStats.NotSentCount);
                }
            }
            catch (Exception e)
            {
                SendMessageStats.AddException(e);

                Interlocked.Increment(ref SendMessageStats.ErrorCount);
            }
        }
    }
}
