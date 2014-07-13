// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using StackExchange.Redis;

using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.AspNet.SignalR.Tracing;

namespace Microsoft.AspNet.SignalR.Redis
{
    /// <summary>
    /// Uses Redis pub-sub to scale-out SignalR applications in web farms.
    /// </summary>
    public class RedisMessageBus : ScaleoutMessageBus
    {
        private const int DefaultBufferSize = 1000;

        private readonly int _database;
        private readonly string _key;
        private readonly Func<ConfigurationOptions> _configurationOptionsFactory;
        private readonly TraceSource _trace;

        private ConnectionMultiplexer _connection;

        private StackExchange.Redis.ISubscriber _channel;
        private int _state;
        private readonly object _callbackLock = new object();

        [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors", Justification = "Reviewed")]
        public RedisMessageBus(IDependencyResolver resolver, RedisScaleoutConfiguration configuration)
            : base(resolver, configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }
                        
            _configurationOptionsFactory = configuration.ConfigurationOptionsFactory;
            _database = configuration.Database;
            _key = configuration.EventKey;
           
            var traceManager = resolver.Resolve<ITraceManager>();
            _trace = traceManager["SignalR." + typeof(RedisMessageBus).Name];

            ReconnectDelay = TimeSpan.FromSeconds(2);
            ConnectWithRetry();
        }

        public TimeSpan ReconnectDelay { get; set; }

        protected override Task Send(int streamIndex, IList<Message> messages)
        {
            var database = _connection.GetDatabase(_database);

            var keys = new RedisKey[] { _key };
            var arguments = new RedisValue[] { RedisMessage.ToBytes(messages) };
            return database.ScriptEvaluateAsync(
                @"local newId = redis.call('INCR', KEYS[1])
                  local payload = newId .. ' ' .. ARGV[1]
                  return redis.call('PUBLISH', KEYS[1], payload)",
                keys,
                arguments);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var oldState = Interlocked.Exchange(ref _state, State.Disposing);

                switch (oldState)
                {
                    case State.Connected:
                        Shutdown();
                        break;
                    case State.Closed:
                    case State.Disposing:
                        // No-op
                        break;
                    case State.Disposed:
                        Interlocked.Exchange(ref _state, State.Disposed);
                        break;
                    default:
                        break;
                }
            }

            base.Dispose(disposing);
        }

        private void Shutdown()
        {
            _trace.TraceInformation("Shutdown()");

            if (_channel != null)
            {
                _channel.Unsubscribe(_key);
                _channel = null;
            }

            if (_connection != null)
            {
                _connection.Close(allowCommandsToComplete:false);
            }

            Interlocked.Exchange(ref _state, State.Disposed);
        }
        
        private void OnConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
            _trace.TraceError("OnConnectionError - " + e.FailureType + ". " + e.Exception.GetBaseException());

            AttemptReconnect(e.Exception);
        }

        private void AttemptReconnect(Exception exception)
        {
            // Change the state to closed and retry connecting
            var oldState = Interlocked.CompareExchange(ref _state,
                                                       State.Closed,
                                                       State.Connected);
            if (oldState == State.Connected)
            {
                _trace.TraceInformation("Attempting reconnect...");

                // Let the base class know that an error occurred
                OnError(0, exception);

                // Retry until the connection reconnects
                ConnectWithRetry();
            }
        }

        private void OnMessage(string key, byte[] data)
        {
            // The key is the stream id (channel)
            var message = RedisMessage.FromBytes(data);

            // locked to avoid overlapping calls (even though we have set the mode 
            // to preserve order on the subscription)
            lock (_callbackLock)
            {
                OnReceived(0, message.Id, message.ScaleoutMessage);
            }
        }

        private void ConnectWithRetry()
        {
            Task connectTask = ConnectToRedis();

            connectTask.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _trace.TraceError("Error connecting to Redis - " + task.Exception.GetBaseException());

                    if (_state == State.Disposing)
                    {
                        Shutdown();
                        return;
                    }

                    TaskAsyncHelper.Delay(ReconnectDelay)
                                   .Then(bus => bus.ConnectWithRetry(), this);
                }
                else
                {
                    var oldState = Interlocked.CompareExchange(ref _state,
                                                               State.Connected,
                                                               State.Closed);
                    if (oldState == State.Closed)
                    {
                        Open(0);
                    }
                    else if (oldState == State.Disposing)
                    {
                        Shutdown();
                    }
                }
            },
            TaskContinuationOptions.ExecuteSynchronously);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Exceptions are caught")]
        private Task ConnectToRedis()
        {
            if (_connection != null)
            {
                _connection.ConnectionFailed -= OnConnectionFailed;
                _connection.Dispose();
                _connection = null;
            }
                      
            try
            {
                _trace.TraceInformation("Connecting...");

                return ConnectionMultiplexer.ConnectAsync(_configurationOptionsFactory()).Then((connection) =>
                {
                    connection.ConnectionFailed += OnConnectionFailed;

                    _trace.TraceInformation("Connection opened");

                    // Create a subscription channel in redis
                    _channel = connection.GetSubscriber();
                    connection.PreserveAsyncOrder = true;

                    _channel.Subscribe(_key, (channel, message) => OnMessage(channel, message));
                    _trace.TraceVerbose("Subscribed to event " + _key);

                    _connection = connection;
                });
            }
            catch (Exception ex)
            {
                _trace.TraceError("Error connecting to Redis - " + ex.GetBaseException());

                return TaskAsyncHelper.FromError(ex);
            }
        }

        private static class State
        {
            public const int Closed = 0;
            public const int Connected = 1;
            public const int Disposing = 2;
            public const int Disposed = 3;
        }
    }
}
