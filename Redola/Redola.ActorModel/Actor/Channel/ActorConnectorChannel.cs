﻿using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Logrila.Logging;
using Redola.ActorModel.Framing;

namespace Redola.ActorModel
{
    public class ActorConnectorChannel : IActorChannel, IDisposable
    {
        private ILog _log = Logger.Get<ActorConnectorChannel>();
        private ActorIdentity _localActor;
        private ActorIdentity _remoteActor;
        private ActorTransportConnector _connector;
        private ActorChannelConfiguration _channelConfiguration;

        private readonly SemaphoreSlim _keepAliveLocker = new SemaphoreSlim(1, 1);
        private KeepAliveTracker _keepAliveTracker;
        private Timer _keepAliveTimeoutTimer;
        private bool _disposed = false;

        public ActorConnectorChannel(
            ActorIdentity localActor,
            ActorTransportConnector remoteConnector,
            ActorChannelConfiguration channelConfiguration)
        {
            if (localActor == null)
                throw new ArgumentNullException("localActor");
            if (remoteConnector == null)
                throw new ArgumentNullException("remoteConnector");
            if (channelConfiguration == null)
                throw new ArgumentNullException("channelConfiguration");

            _localActor = localActor;
            _connector = remoteConnector;
            _channelConfiguration = channelConfiguration;

            _keepAliveTracker = KeepAliveTracker.Create(KeepAliveInterval, new TimerCallback((s) => OnKeepAlive()));
            _keepAliveTimeoutTimer = new Timer(new TimerCallback((s) => OnKeepAliveTimeout()), null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Identifier
        {
            get
            {
                return string.Format("Connect#{0}", _connector.ConnectToEndPoint.ToString());
            }
        }

        public ActorIdentity LocalActor
        {
            get
            {
                return _localActor;
            }
        }

        public ActorIdentity RemoteActor
        {
            get
            {
                return _remoteActor;
            }
        }

        public bool Active
        {
            get
            {
                return _connector.IsConnected && IsHandshaked;
            }
        }

        public IPEndPoint ConnectToEndPoint
        {
            get
            {
                return _connector.ConnectToEndPoint;
            }
        }

        public bool IsHandshaked { get; private set; }

        public void Open()
        {
            try
            {
                if (_connector.IsConnected)
                    return;

                _connector.TransportConnected += OnTransportConnected;
                _connector.TransportDisconnected += OnTransportDisconnected;

                _connector.Connect();

                OnOpen();
            }
            catch (TimeoutException)
            {
                _log.ErrorFormat("Connect remote [{0}] timeout with [{1}].",
                    this.ConnectToEndPoint, _connector.TransportConfiguration.ConnectTimeout);
                Close();
            }
        }

        public void Close()
        {
            try
            {
                if (_keepAliveTracker != null)
                {
                    _keepAliveTracker.StopTimer();
                }
                if (_keepAliveTimeoutTimer != null)
                {
                    _keepAliveTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }

                _connector.TransportConnected -= OnTransportConnected;
                _connector.TransportDisconnected -= OnTransportDisconnected;
                _connector.TransportDataReceived -= OnTransportDataReceived;

                if (_connector.IsConnected)
                {
                    _connector.Disconnect();
                }

                if (_remoteActor != null)
                {
                    if (ChannelDisconnected != null)
                    {
                        ChannelDisconnected(this, new ActorChannelDisconnectedEventArgs(this.Identifier, _remoteActor));
                    }
                }

                _log.DebugFormat("Disconnected with remote [{0}], SessionKey[{1}].", _remoteActor, this.ConnectToEndPoint);
            }
            finally
            {
                _remoteActor = null;
                IsHandshaked = false;

                OnClose();
            }
        }

        protected virtual void OnOpen()
        {
        }

        protected virtual void OnClose()
        {
        }

        private void Handshake()
        {
            Handshake(TimeSpan.FromSeconds(5));
        }

        private void Handshake(TimeSpan timeout)
        {
            var actorHandshakeRequestData = _channelConfiguration.FrameBuilder.ControlFrameDataEncoder.EncodeFrameData(_localActor);
            var actorHandshakeRequest = new HelloFrame(actorHandshakeRequestData);
            var actorHandshakeRequestBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorHandshakeRequest);

            ManualResetEventSlim waitingHandshaked = new ManualResetEventSlim(false);
            ActorTransportDataReceivedEventArgs handshakeResponseEvent = null;
            EventHandler<ActorTransportDataReceivedEventArgs> onHandshaked =
                (s, e) =>
                {
                    handshakeResponseEvent = e;
                    waitingHandshaked.Set();
                };

            _connector.TransportDataReceived += onHandshaked;
            _log.DebugFormat("Handshake request from local actor [{0}].", _localActor);
            _connector.Send(actorHandshakeRequestBuffer);

            bool handshaked = waitingHandshaked.Wait(timeout);
            _connector.TransportDataReceived -= onHandshaked;
            waitingHandshaked.Dispose();

            if (handshaked && handshakeResponseEvent != null)
            {
                ActorFrameHeader actorHandshakeResponseFrameHeader = null;
                bool isHeaderDecoded = _channelConfiguration.FrameBuilder.TryDecodeFrameHeader(
                    handshakeResponseEvent.Data, handshakeResponseEvent.DataOffset, handshakeResponseEvent.DataLength,
                    out actorHandshakeResponseFrameHeader);
                if (isHeaderDecoded && actorHandshakeResponseFrameHeader.OpCode == OpCode.Welcome)
                {
                    byte[] payload;
                    int payloadOffset;
                    int payloadCount;
                    _channelConfiguration.FrameBuilder.DecodePayload(
                        handshakeResponseEvent.Data, handshakeResponseEvent.DataOffset, actorHandshakeResponseFrameHeader,
                        out payload, out payloadOffset, out payloadCount);
                    var actorHandshakeResponseData = _channelConfiguration.FrameBuilder.ControlFrameDataDecoder.DecodeFrameData<ActorIdentity>(
                        payload, payloadOffset, payloadCount);

                    _remoteActor = actorHandshakeResponseData;
                    _log.DebugFormat("Handshake response from remote actor [{0}].", _remoteActor);
                }

                if (_remoteActor == null)
                {
                    _log.ErrorFormat("Handshake with remote [{0}] failed, invalid actor description.", this.ConnectToEndPoint);
                    Close();
                }
                else
                {
                    _log.DebugFormat("Handshake with remote [{0}] successfully, RemoteActor[{1}].", this.ConnectToEndPoint, _remoteActor);

                    IsHandshaked = true;
                    if (ChannelConnected != null)
                    {
                        ChannelConnected(this, new ActorChannelConnectedEventArgs(this.Identifier, _remoteActor));
                    }

                    _connector.TransportDataReceived += OnTransportDataReceived;
                    _keepAliveTracker.StartTimer();
                }
            }
            else
            {
                _log.ErrorFormat("Handshake with remote [{0}] timeout [{1}].", this.ConnectToEndPoint, timeout);
                Close();
            }
        }

        protected virtual void OnTransportConnected(object sender, ActorTransportConnectedEventArgs e)
        {
            Task.Factory.StartNew(() => { Handshake(); }, TaskCreationOptions.PreferFairness);
        }

        protected virtual void OnTransportDisconnected(object sender, ActorTransportDisconnectedEventArgs e)
        {
            Close();
        }

        protected virtual void OnTransportDataReceived(object sender, ActorTransportDataReceivedEventArgs e)
        {
            _keepAliveTracker.OnDataReceived();
            StopKeepAliveTimeoutTimer(); // intend to disable keep-alive timeout when receive anything

            ActorFrameHeader actorKeepAliveRequestFrameHeader = null;
            bool isHeaderDecoded = _channelConfiguration.FrameBuilder.TryDecodeFrameHeader(
                e.Data, e.DataOffset, e.DataLength,
                out actorKeepAliveRequestFrameHeader);
            if (isHeaderDecoded && actorKeepAliveRequestFrameHeader.OpCode == OpCode.Ping)
            {
                _log.DebugFormat("KeepAlive receive request from remote actor [{0}] to local actor [{1}].", _remoteActor, _localActor);

                var actorKeepAliveResponse = new PongFrame();
                var actorKeepAliveResponseBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorKeepAliveResponse);

                _log.DebugFormat("KeepAlive send response from local actor [{0}] to remote actor [{1}].", _localActor, _remoteActor);
                _connector.Send(actorKeepAliveResponseBuffer);
            }
            else if (isHeaderDecoded && actorKeepAliveRequestFrameHeader.OpCode == OpCode.Pong)
            {
                _log.DebugFormat("KeepAlive receive response from remote actor [{0}] to local actor [{1}].", _remoteActor, _localActor);
                StopKeepAliveTimeoutTimer();
            }
            else
            {
                if (ChannelDataReceived != null)
                {
                    ChannelDataReceived(this, new ActorChannelDataReceivedEventArgs(this.Identifier, _remoteActor, e.Data, e.DataOffset, e.DataLength));
                }
            }
        }

        public event EventHandler<ActorChannelConnectedEventArgs> ChannelConnected;
        public event EventHandler<ActorChannelDisconnectedEventArgs> ChannelDisconnected;
        public event EventHandler<ActorChannelDataReceivedEventArgs> ChannelDataReceived;

        #region Send

        public void Send(string identifier, byte[] data)
        {
            Send(identifier, data, 0, data.Length);
        }

        public void Send(string identifier, byte[] data, int offset, int count)
        {
            if (this.Identifier != identifier)
                throw new InvalidOperationException(
                    string.Format("Channel identifier is not matched, Identifier[{0}].", identifier));

            _connector.Send(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public void BeginSend(string identifier, byte[] data)
        {
            BeginSend(identifier, data, 0, data.Length);
        }

        public void BeginSend(string identifier, byte[] data, int offset, int count)
        {
            if (this.Identifier != identifier)
                throw new InvalidOperationException(
                    string.Format("Channel identifier is not matched, Identifier[{0}].", identifier));

            _connector.BeginSend(data, offset, count);
            _keepAliveTracker.OnDataSent();
        }

        public IAsyncResult BeginSend(string identifier, byte[] data, AsyncCallback callback, object state)
        {
            return BeginSend(identifier, data, 0, data.Length, callback, state);
        }

        public IAsyncResult BeginSend(string identifier, byte[] data, int offset, int count, AsyncCallback callback, object state)
        {
            if (this.Identifier != identifier)
                throw new InvalidOperationException(
                    string.Format("Channel identifier is not matched, Identifier[{0}].", identifier));

            var ar = _connector.BeginSend(data, offset, count, callback, state);
            _keepAliveTracker.OnDataSent();

            return ar;
        }

        public void EndSend(string identifier, IAsyncResult asyncResult)
        {
            if (this.Identifier != identifier)
                throw new InvalidOperationException(
                    string.Format("Channel identifier is not matched, Identifier[{0}].", identifier));

            _connector.EndSend(asyncResult);
        }

        #endregion

        #region Keep Alive

        public TimeSpan KeepAliveInterval { get { return _channelConfiguration.KeepAliveInterval; } }

        public TimeSpan KeepAliveTimeout { get { return _channelConfiguration.KeepAliveTimeout; } }

        public bool KeepAliveEnabled { get { return _channelConfiguration.KeepAliveEnabled; } }

        private void StartKeepAliveTimeoutTimer()
        {
            _keepAliveTimeoutTimer.Change((int)KeepAliveTimeout.TotalMilliseconds, Timeout.Infinite);
        }

        private void StopKeepAliveTimeoutTimer()
        {
            _keepAliveTimeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnKeepAliveTimeout()
        {
            _log.ErrorFormat("Keep-alive timer timeout [{0}].", KeepAliveTimeout);
            Close();
        }

        private void OnKeepAlive()
        {
            if (_keepAliveLocker.Wait(0))
            {
                try
                {
                    if (!Active)
                        return;

                    if (!KeepAliveEnabled)
                        return;

                    if (_localActor.Type == _remoteActor.Type
                        && _localActor.Name == _remoteActor.Name)
                        return;

                    if (_keepAliveTracker.ShouldSendKeepAlive())
                    {
                        var actorKeepAliveRequest = new PingFrame();
                        var actorKeepAliveRequestBuffer = _channelConfiguration.FrameBuilder.EncodeFrame(actorKeepAliveRequest);

                        _log.DebugFormat("KeepAlive send request from local actor [{0}] to remote actor [{1}].", _localActor, _remoteActor);

                        _connector.Send(actorKeepAliveRequestBuffer);
                        StartKeepAliveTimeoutTimer();

                        _keepAliveTracker.ResetTimer();
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex.Message, ex);
                    Close();
                }
                finally
                {
                    _keepAliveLocker.Release();
                }
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        if (_keepAliveTracker != null)
                        {
                            _keepAliveTracker.Dispose();
                        }
                        if (_keepAliveTimeoutTimer != null)
                        {
                            _keepAliveTimeoutTimer.Dispose();
                        }
                    }
                    catch { }
                }

                _disposed = true;
            }
        }

        #endregion

        public override string ToString()
        {
            return this.Identifier;
        }
    }
}
