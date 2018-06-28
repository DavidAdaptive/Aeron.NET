﻿/*
 * Copyright 2014 - 2017 Adaptive Financial Consulting Ltd
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0S
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Adaptive.Aeron.Exceptions;
using Adaptive.Aeron.Status;
using Adaptive.Agrona;
using Adaptive.Agrona.Collections;
using Adaptive.Agrona.Concurrent;
using Adaptive.Agrona.Concurrent.Status;

[assembly: InternalsVisibleTo("Adaptive.Aeron.Tests")]

namespace Adaptive.Aeron
{
    /// <summary>
    /// Client conductor receives responses and notifications from Media Driver and acts on them in addition to forwarding
    /// commands from the Client API to the Media Driver conductor.
    /// </summary>
    internal class ClientConductor : IAgent, IDriverEventsListener
    {
        private const long NO_CORRELATION_ID = Aeron.NULL_VALUE;
        private static readonly long RESOURCE_CHECK_INTERVAL_NS = 1;
        private static readonly long RESOURCE_LINGER_NS = 3;

        private readonly long _keepAliveIntervalNs;
        private readonly long _driverTimeoutMs;
        private readonly long _driverTimeoutNs;
        private readonly long _interServiceTimeoutNs;
        private long _timeOfLastKeepAliveNs;
        private long _timeOfLastResourcesCheckNs;
        private long _timeOfLastServiceNs;
        private bool _isClosed;
        private string _stashedChannel;
        private RegistrationException _driverException;

        private readonly Aeron.Context _ctx;
        private readonly ILock _clientLock;
        private readonly IEpochClock _epochClock;
        private readonly INanoClock _nanoClock;
        private readonly DriverEventsAdapter _driverEventsAdapter;
        private readonly ILogBuffersFactory _logBuffersFactory;
        private readonly IDictionary<long, LogBuffers> _logBuffersByIdMap = new DefaultDictionary<long, LogBuffers>(null);
        private readonly IDictionary<long, object> _resourceByRegIdMap = new DefaultDictionary<long, object>(null);
        private readonly List<IManagedResource> _lingeringResources = new List<IManagedResource>();
        private readonly AvailableImageHandler _defaultAvailableImageHandler;
        private readonly UnavailableImageHandler _defaultUnavailableImageHandler;
        private readonly AvailableCounterHandler _availableCounterHandler;
        private readonly UnavailableCounterHandler _unavailableCounterHandler;
        private readonly DriverProxy _driverProxy;
        private readonly UnsafeBuffer _counterValuesBuffer;
        private readonly CountersReader _countersReader;

        internal ClientConductor()
        {
        }

        internal ClientConductor(Aeron.Context ctx)
        {
            _ctx = ctx;

            _clientLock = ctx.ClientLock();
            _epochClock = ctx.EpochClock();
            _nanoClock = ctx.NanoClock();

            _driverProxy = ctx.DriverProxy();
            _logBuffersFactory = ctx.LogBuffersFactory();

            _keepAliveIntervalNs = ctx.KeepAliveInterval();
            _driverTimeoutMs = ctx.DriverTimeoutMs();
            _driverTimeoutNs = _driverTimeoutMs * 1000000;
            _interServiceTimeoutNs = ctx.InterServiceTimeout();
            _defaultAvailableImageHandler = ctx.AvailableImageHandler();
            _defaultUnavailableImageHandler = ctx.UnavailableImageHandler();
            _availableCounterHandler = ctx.AvailableCounterHandler();
            _unavailableCounterHandler = ctx.UnavailableCounterHandler();
            _driverEventsAdapter = new DriverEventsAdapter(ctx.ToClientBuffer(), this);
            _counterValuesBuffer = ctx.CountersValuesBuffer();
            _countersReader =
                new CountersReader(ctx.CountersMetaDataBuffer(), ctx.CountersValuesBuffer(), Encoding.ASCII);

            long nowNs = _nanoClock.NanoTime();
            _timeOfLastKeepAliveNs = nowNs;
            _timeOfLastResourcesCheckNs = nowNs;
            _timeOfLastServiceNs = nowNs;
        }

        public void OnStart()
        {
            // Do Nothing
        }

        public void OnClose()
        {
            _clientLock.Lock();

            try
            {
                if (!_isClosed)
                {
                    _isClosed = true;

                    int lingeringResourcesSize = _lingeringResources.Count;
                    ForceCloseResources();
                    _driverProxy.ClientClose();

                    if (_lingeringResources.Count > lingeringResourcesSize)
                    {
                        Aeron.Sleep(1);
                    }

                    for (int i = 0, size = _lingeringResources.Count; i < size; i++)
                    {
                        _lingeringResources[i].Delete();
                    }
                }
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        public int DoWork()
        {
            int workCount = 0;

            if (_clientLock.TryLock())
            {
                try
                {
                    if (_isClosed)
                    {
                        throw new AgentTerminationException();
                    }

                    workCount = Service(NO_CORRELATION_ID);
                }
                finally
                {
                    _clientLock.Unlock();
                }
            }

            return workCount;
        }

        public string RoleName()
        {
            return "aeron-client-conductor";
        }

        public bool IsClosed()
        {
            return _isClosed;
        }

        public void OnError(long correlationId, ErrorCode errorCode, string message)
        {
            _driverException = new RegistrationException(errorCode, message);
        }

        public void OnChannelEndpointError(int statusIndicatorId, string message)
        {
            foreach (var resource in _resourceByRegIdMap.Values)
            {
                if (resource is Subscription subscription)
                {
                    if (subscription.ChannelStatusId == statusIndicatorId)
                    {
                        HandleError(new ChannelEndpointException(statusIndicatorId, message));
                    }
                }
                else if (resource is Publication publication)
                {
                    if (publication.ChannelStatusId() == statusIndicatorId)
                    {
                        HandleError(new ChannelEndpointException(statusIndicatorId, message));
                    }
                }
            }
        }

        public void OnNewPublication(
            long correlationId,
            long registrationId,
            int streamId,
            int sessionId,
            int publicationLimitId,
            int statusIndicatorId,
            string logFileName)
        {
            var publication = new ConcurrentPublication(
                this,
                _stashedChannel,
                streamId,
                sessionId,
                new UnsafeBufferPosition(_counterValuesBuffer, publicationLimitId),
                statusIndicatorId,
                LogBuffers(registrationId, logFileName),
                registrationId,
                correlationId
            );
            
            _resourceByRegIdMap[correlationId] = publication;
        }

        public void OnNewExclusivePublication(
            long correlationId,
            long registrationId,
            int streamId,
            int sessionId,
            int publicationLimitId,
            int statusIndicatorId,
            string logFileName)
        {
            var publication = new ExclusivePublication(
                this,
                _stashedChannel,
                streamId,
                sessionId,
                new UnsafeBufferPosition(_counterValuesBuffer, publicationLimitId),
                statusIndicatorId,
                LogBuffers(registrationId, logFileName),
                registrationId,
                correlationId
            );
            
            _resourceByRegIdMap[correlationId] = publication;
        }

        public void OnNewSubscription(long correlationId, int statusIndicatorId)
        {
            Subscription subscription = (Subscription) _resourceByRegIdMap[correlationId];
            subscription.ChannelStatusId = statusIndicatorId;
        }

        public void OnAvailableImage(
            long correlationId,
            int streamId,
            int sessionId,
            long subscriptionRegistrationId,
            int subscriberPositionId,
            string logFileName,
            string sourceIdentity)
        {
            Subscription subscription = (Subscription) _resourceByRegIdMap[subscriptionRegistrationId];
            if (null != subscription && !subscription.ContainsImage(correlationId))
            {
                Image image = new Image(subscription, sessionId,
                    new UnsafeBufferPosition(_counterValuesBuffer, subscriberPositionId),
                    LogBuffers(correlationId, logFileName), _ctx.ErrorHandler(), sourceIdentity, correlationId);

                AvailableImageHandler handler = subscription.AvailableImageHandler();
                if (null != handler)
                {
                    try
                    {
                        handler(image);
                    }
                    catch (Exception ex)
                    {
                        HandleError(ex);
                    }
                }

                subscription.AddImage(image);
            }
        }

        public void OnUnavailableImage(long correlationId, long subscriptionRegistrationId, int streamId)
        {
            Subscription subscription = (Subscription) _resourceByRegIdMap[subscriptionRegistrationId];
            if (null != subscription)
            {
                Image image = subscription.RemoveImage(correlationId);
                if (null != image)
                {
                    UnavailableImageHandler handler = subscription.UnavailableImageHandler();
                    if (null != handler)
                    {
                        try
                        {
                            handler(image);
                        }
                        catch (Exception ex)
                        {
                            HandleError(ex);
                        }
                    }
                }
            }
        }

        public void OnNewCounter(long correlationId, int counterId)
        {
            _resourceByRegIdMap[correlationId] = new Counter(correlationId, this, _counterValuesBuffer, counterId);
            OnAvailableCounter(correlationId, counterId);
        }

        public void OnAvailableCounter(long registrationId, int counterId)
        {
            if (null != _availableCounterHandler)
            {
                try
                {
                    _availableCounterHandler(_countersReader, registrationId, counterId);
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                }
            }
        }

        public void OnUnavailableCounter(long registrationId, int counterId)
        {
            if (null != _unavailableCounterHandler)
            {
                try
                {
                    _unavailableCounterHandler(_countersReader, registrationId, counterId);
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                }
            }
        }

        internal CountersReader CountersReader()
        {
            return _countersReader;
        }

        internal void HandleError(Exception ex)
        {
            _ctx.ErrorHandler()(ex);
        }

        internal ConcurrentPublication AddPublication(string channel, int streamId)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                _stashedChannel = channel;
                long registrationId = _driverProxy.AddPublication(channel, streamId);
                AwaitResponse(registrationId);

                return (ConcurrentPublication) _resourceByRegIdMap[registrationId]; // TODO dictionary semantics if non-existant
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual ExclusivePublication AddExclusivePublication(string channel, int streamId)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                _stashedChannel = channel;
                long registrationId = _driverProxy.AddExclusivePublication(channel, streamId);
                AwaitResponse(registrationId);

                return (ExclusivePublication) _resourceByRegIdMap[registrationId];
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void ReleasePublication(Publication publication)
        {
            _clientLock.Lock();
            try
            {
                if (!publication.IsClosed)
                {
                    publication.InternalClose();

                    EnsureOpen();

                    var removedPublication = _resourceByRegIdMap[publication.RegistrationId];

                    if (_resourceByRegIdMap.Remove(publication.RegistrationId) && publication == removedPublication)
                    {
                        ReleaseLogBuffers(publication.LogBuffers(), publication.OriginalRegistrationId());
                        AwaitResponse(_driverProxy.RemovePublication(publication.RegistrationId));
                    }
                }
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal Subscription AddSubscription(string channel, int streamId)
        {
            return AddSubscription(channel, streamId, _defaultAvailableImageHandler, _defaultUnavailableImageHandler);
        }

        internal virtual Subscription AddSubscription(string channel, int streamId, AvailableImageHandler availableImageHandler, UnavailableImageHandler unavailableImageHandler)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                long correlationId = _driverProxy.AddSubscription(channel, streamId);
                Subscription subscription = new Subscription(this, channel, streamId, correlationId, availableImageHandler, unavailableImageHandler);

                _resourceByRegIdMap[correlationId] = subscription;

                AwaitResponse(correlationId);

                return subscription;
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void ReleaseSubscription(Subscription subscription)
        {
            _clientLock.Lock();
            try
            {
                if (!subscription.Closed)
                {
                    subscription.InternalClose();

                    EnsureOpen();

                    long registrationId = subscription.RegistrationId;
                    AwaitResponse(_driverProxy.RemoveSubscription(registrationId));
                    _resourceByRegIdMap.Remove(registrationId);
                }
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void AddDestination(long registrationId, string endpointChannel)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                AwaitResponse(_driverProxy.AddDestination(registrationId, endpointChannel));
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void RemoveDestination(long registrationId, string endpointChannel)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                AwaitResponse(_driverProxy.RemoveDestination(registrationId, endpointChannel));
            }
            finally
            {
                _clientLock.Unlock();
            }
        }
        
        internal void AddRcvDestination(long registrationId, string endpointChannel)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                AwaitResponse(_driverProxy.AddRcvDestination(registrationId, endpointChannel));
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal void RemoveRcvDestination(long registrationId, string endpointChannel)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                AwaitResponse(_driverProxy.RemoveRcvDestination(registrationId, endpointChannel));
            }
            finally
            {
                _clientLock.Unlock();
            }
        }


        internal virtual Counter AddCounter(int typeId, IDirectBuffer keyBuffer, int keyOffset, int keyLength, IDirectBuffer labelBuffer, int labelOffset, int labelLength)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                if (keyLength < 0 || keyLength > CountersManager.MAX_KEY_LENGTH)
                {
                    throw new ArgumentException("key length out of bounds: " + keyLength);
                }

                if (labelLength < 0 || labelLength > CountersManager.MAX_LABEL_LENGTH)
                {
                    throw new ArgumentException("label length out of bounds: " + labelLength);
                }

                long registrationId = _driverProxy.AddCounter(typeId, keyBuffer, keyOffset, keyLength, labelBuffer, labelOffset, labelLength);

                AwaitResponse(registrationId);

                return (Counter) _resourceByRegIdMap[registrationId];
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal Counter AddCounter(int typeId, string label)
        {
            _clientLock.Lock();
            try
            {
                EnsureOpen();

                if (label.Length > CountersManager.MAX_LABEL_LENGTH)
                {
                    throw new ArgumentException("label length exceeds MAX_LABEL_LENGTH: " + label.Length);
                }

                long registrationId = _driverProxy.AddCounter(typeId, label);

                AwaitResponse(registrationId);

                return (Counter) _resourceByRegIdMap[registrationId];
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void ReleaseCounter(Counter counter)
        {
            _clientLock.Lock();
            try
            {
                if (!counter.IsClosed())
                {
                    counter.InternalClose();

                    EnsureOpen();

                    long registrationId = counter.RegistrationId();
                    AwaitResponse(_driverProxy.RemoveCounter(registrationId));
                    _resourceByRegIdMap.Remove(registrationId);
                }
            }
            finally
            {
                _clientLock.Unlock();
            }
        }

        internal virtual void ReleaseImage(Image image)
        {
            ReleaseLogBuffers(image.LogBuffers(), image.CorrelationId);
        }

        internal virtual void ReleaseLogBuffers(LogBuffers logBuffers, long registrationId)
        {
            if (logBuffers.DecRef() == 0)
            {
                logBuffers.TimeOfLastStateChange(_nanoClock.NanoTime());
                _logBuffersByIdMap.Remove(registrationId);
                _lingeringResources.Add(logBuffers);
            }
        }

        internal DriverEventsAdapter DriverListenerAdapter()
        {
            return _driverEventsAdapter;
        }

        internal virtual long ChannelStatus(int channelStatusId)
        {
            switch (channelStatusId)
            {
                case 0:
                    return ChannelEndpointStatus.INITIALIZING;

                case ChannelEndpointStatus.NO_ID_ALLOCATED:
                    return ChannelEndpointStatus.ACTIVE;

                default:
                    return _countersReader.GetCounterValue(channelStatusId);
            }
        }

        private void EnsureOpen()
        {
            if (_isClosed)
            {
                throw new InvalidOperationException("Aeron client conductor is closed");
            }
        }

        private LogBuffers LogBuffers(long registrationId, string logFileName)
        {
            LogBuffers logBuffers = _logBuffersByIdMap[registrationId];
            if (null == logBuffers)
            {
                logBuffers = _logBuffersFactory.Map(logFileName);
                _logBuffersByIdMap[registrationId] = logBuffers;
            }

            logBuffers.IncRef();

            return logBuffers;
        }


        private int Service(long correlationId)
        {
            int workCount = 0;

            try
            {
                workCount += OnCheckTimeouts();
                workCount += _driverEventsAdapter.Receive(correlationId);
            }
            catch (Exception throwable)
            {
                HandleError(throwable);

                if (IsClientApiCall(correlationId))
                {
                    throw;
                }
            }

            return workCount;
        }

        private static bool IsClientApiCall(long correlationId)
        {
            return correlationId != NO_CORRELATION_ID;
        }

        private void AwaitResponse(long correlationId)
        {
            _driverException = null;
            var deadlineNs = _nanoClock.NanoTime() + _driverTimeoutNs;

            do
            {
                Aeron.Sleep(1);

                Service(correlationId);

                if (_driverEventsAdapter.LastReceivedCorrelationId() == correlationId)
                {
                    if (null != _driverException)
                    {
                        throw _driverException;
                    }

                    return;
                }
            } while (_nanoClock.NanoTime() < deadlineNs);

            throw new DriverTimeoutException("No response from MediaDriver within (ms):" + _driverTimeoutMs);
        }

        private int OnCheckTimeouts()
        {
            int workCount = 0;
            long nowNs = _nanoClock.NanoTime();

            if (nowNs > (_timeOfLastServiceNs + Aeron.IdleSleepNs))
            {
                checkServiceInterval(nowNs);
                _timeOfLastServiceNs = nowNs;

                workCount += checkLiveness(nowNs);
                workCount += checkLingeringResources(nowNs);
            }

            return workCount;
        }

        private void checkServiceInterval(long nowNs)
        {
            if (nowNs > (_timeOfLastServiceNs + _interServiceTimeoutNs))
            {
                int lingeringResourcesSize = _lingeringResources.Count;

                ForceCloseResources();

                if (_lingeringResources.Count > lingeringResourcesSize)
                {
                    Aeron.Sleep(1000);
                }

                OnClose();

                throw new ConductorServiceTimeoutException("Exceeded (ns): " + _interServiceTimeoutNs);
            }
        }

        private int checkLiveness(long nowNs)
        {
            if (nowNs > (_timeOfLastKeepAliveNs + _keepAliveIntervalNs))
            {
                if (_epochClock.Time() > (_driverProxy.TimeOfLastDriverKeepaliveMs() + _driverTimeoutMs))
                {
                    OnClose();

                    throw new DriverTimeoutException("MediaDriver keepalive older than (ms): " + _driverTimeoutMs);
                }

                _driverProxy.SendClientKeepalive();
                _timeOfLastKeepAliveNs = nowNs;

                return 1;
            }

            return 0;
        }

        private int checkLingeringResources(long nowNs)
        {
            if (nowNs > (_timeOfLastResourcesCheckNs + RESOURCE_CHECK_INTERVAL_NS))
            {
                List<IManagedResource> lingeringResources = _lingeringResources;
                for (int lastIndex = lingeringResources.Count - 1, i = lastIndex; i >= 0; i--)
                {
                    IManagedResource resource = lingeringResources[i];

                    if (nowNs > (resource.TimeOfLastStateChange() + RESOURCE_LINGER_NS))
                    {
                        ListUtil.FastUnorderedRemove(lingeringResources, i, lastIndex--);
                        resource.Delete();
                    }
                }

                _timeOfLastResourcesCheckNs = nowNs;

                return 1;
            }

            return 0;
        }

        private void ForceCloseResources()
        {
            foreach (var resource in _resourceByRegIdMap.Values)
            {
                if (resource is Subscription subscription)
                {
                    subscription.InternalClose();
                }
                else if (resource is Publication publication)
                {
                    publication.InternalClose();
                    ReleaseLogBuffers(publication.LogBuffers(), publication.OriginalRegistrationId());
                }
                else if (resource is Counter counter)
                {
                    counter.InternalClose();
                }
            }

            _resourceByRegIdMap.Clear();
        }
    }
}