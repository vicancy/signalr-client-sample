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
using Microsoft.Extensions.Logging;
using System.Linq;

namespace PerformanceTest
{
    enum Status
    {
        Connected,
        Connecting,
        Reconnecting,
        Disconnected,
    }

    class Tester
    {
        private const int MaxReconnectWaitMilliseconds = 3000;
        private const int MinReconnectWaitMilliseconds = 300;
        private static readonly HttpClient _client = new HttpClient();
        private static readonly TimeSpan AuthExpire = TimeSpan.FromMinutes(55);
        protected static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1);

        private DateTimeOffset _lastConnectTime;

        private DateTimeOffset _lastAuthTime = DateTimeOffset.MinValue;

        private readonly string _host;

        private readonly string _userId;

        public HubConnection Connection { get; private set; }

        public StatsInfo ConnectStats { get; private set; }

        public StatsInfo RecoverStats { get; private set; }

        public StatsInfo SendMessageStats { get; private set; }

        public Tester(string host, string userId = null)
        {
            this._host = host;
            if (string.IsNullOrWhiteSpace(userId))
            {
                this._userId = Guid.NewGuid().ToString();
            }
            else
            {
                this._userId = userId;
            }
            ConnectStats = new StatsInfo("connect", userId, logger);
            RecoverStats = new StatsInfo("recover", userId, logger);
            SendMessageStats = new StatsInfo("echo", userId, logger);
        }

        public Status ConnectStatus { get; private set; } = Status.Disconnected;

        public Task Connect()
        {
            return RetryConnect(null);
        }

        private async Task RetryConnect(Exception ex)
        {
            if (!_connectLock.Wait(0))
            {
                // someone is already connecting
                return;
            }

            try
            {
                if (ex != null)
                {
                    ConnectStats.AddException(ex);
                    Interlocked.Increment(ref ConnectStats.ErrorCount);
                }

                var reconnectNow = DateTime.UtcNow;
                while (true)
                {
                    try
                    {
                        // delay a random value < 3s
                        await Task.Delay(new Random((int)Stopwatch.GetTimestamp()).Next(MinReconnectWaitMilliseconds, MaxReconnectWaitMilliseconds));
                        await ConnectCore();
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
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task ConnectCore()
        {
            ConnectStatus = Status.Connecting;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAuthTime > AuthExpire)
            {
                (string url, string accessToken) = await GetAuth();

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(accessToken))
                {
                    throw new ArgumentNullException("url or accesstoken is null");
                }

                if (Connection != null)
                {
                    Connection.Closed -= OnClosed;
                    // 1. dispose the old connection first
                    await Connection.DisposeAsync();
                    Connection = null;
                }

                var connection = new HubConnectionBuilder()
                    .WithUrl(url, options =>
                    //.WithUrl("http://localhost:8080/client?hub=gamehub", options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(accessToken);
                        options.CloseTimeout = TimeSpan.FromSeconds(15);
                        options.Transports = HttpTransportType.WebSockets;
                        options.SkipNegotiation = true;
                    })
                    //.AddMessagePackProtocol()
                    .Build();

                connection.Closed += OnClosed;

                connection.On("Connected", () =>
                {
                    ConnectStatus = Status.Connected;
                    Interlocked.Increment(ref ConnectStats.SuccessCount);

                    var elapsed = ConnectStats.SetElapsed(_lastConnectTime);
                });

                connection.On<MessageBody>("PerformanceTest", message =>
                {
                    Interlocked.Increment(ref SendMessageStats.ReceivedCount);
                    SendMessageStats.SetElapsed(message.CreatedTime);
                });

                // set the auth time after the connection is built
                _lastAuthTime = DateTime.UtcNow;
                Connection = connection;
            }

            _lastConnectTime = DateTime.UtcNow;
            Interlocked.Increment(ref ConnectStats.TotalCount);
            await Connection.StartAsync();
        }

        private Task OnClosed(Exception ex)
        {
            ConnectStatus = Status.Disconnected;
            return RetryConnect(ex);
        }

        private async Task<(string, string)> GetAuth()
        {
            string result = await _client.GetStringAsync($"{_host}/auth/login/?username={_userId}&password=admin");

            if (string.IsNullOrEmpty(result))
            {
                logger.Error($"用户 {_userId}登录失败");
                return (null, null);
            }

            try
            {
                var accessInfo = JsonConvert.DeserializeObject<AccessInfo>(result);
                if (!string.IsNullOrEmpty(accessInfo.Error))
                {
                    logger.Error($"用户 {_userId} 登录失败," + accessInfo.Error);
                    accessInfo = null;
                    return (null, null);
                }
                return (accessInfo.SignalRUrl, accessInfo.SignalRToken);
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"用户 {_userId} 登录失败");
                return (null, null);
            }
        }

        public async Task RunTest(byte[] content)
        {
            try
            {
                if (ConnectStatus == Status.Connected)
                {
                    await Connection.SendAsync("PerformanceTest", new MessageBody
                    {
                        CreatedTime = DateTimeOffset.UtcNow,
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
