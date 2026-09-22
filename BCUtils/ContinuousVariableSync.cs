using System;
using System.IO;
using System.Runtime.CompilerServices;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

namespace com.superneko.bcutils
{
    /// <summary>
    /// Reliable variable sync. Owner republishes value repeatedly so followers can
    /// grab update event. When the owner stopped sending value, followes tries
    /// to take the ownership so someone is almost always having ownership.
    /// If there's no player that can take the ownership, variables are fall backed
    /// into its default value (it happens when there's no player with ownership
    /// permission). Suitable for important game logics or globally affected switches.
    /// - Complexity: Relatively high
    ///   - Almost everything is about safety check and optimization. underlying
    ///     model is relatively simple.
    /// - Bandwidth: High
    /// - Reliability: High
    /// </summary>
    [Cilboxable]
    public abstract class ContinuousVariableSync : VariableSyncBase
    {
        // Message headers
        const byte NMSG_VALUE = 0;
        const byte NMSG_CUSTOM_EVENT = 1;

        // Task intervals
        const float REPUBLISH_INTERVAL_SEC = 1f;
        const float OWNER_CHECK_INTERVAL_SEC = 1f;

        // Frame rate considered to be "too low". When the local player freezes,
        // Ownership may be invalidated before value packets processed if there
        // is only the Time.time invalidation exists.
        // To avoid this, we check that enough frame is consumed before the
        // invalidation.
        const int LOW_FPS = 10;

        const float OWNERSHIP_INVALIDATION_SEC = 5f;
        const int OWNERSHIP_INVALIDATION_FRAME = (int)(OWNERSHIP_INVALIDATION_SEC * LOW_FPS);
        const float UNINITIALIZED_OWNERSHIP_INVALIDATION_SEC = 10f;
        const int UNINITIALIZED_OWNERSHIP_INVALIDATION_FRAME = (int)(UNINITIALIZED_OWNERSHIP_INVALIDATION_SEC * LOW_FPS);

        // Everything is completely fxxked. Fall back to initial value.
        const float VALUE_FALLBACK_SEC = 20f;
        const int VALUE_FALLBACK_FRAME = (int)(VALUE_FALLBACK_SEC * LOW_FPS);

        const float OWNERSHIP_ELECTION_DELAY_MAX = 3f;
        const float OWNERSHIP_ACQUIRE_TIMEOUT_SEC = 1f;

        class CancelSignal
        {
            public bool Cancelled { get; private set; }

            public void Cancel()
            {
                Cancelled = true;
            }
        }

        enum State
        {
            NETWORK_IS_NOT_READY,
            WAITING_FOR_INITIAL_PUBLISH,
            SYNCED,
            RECOVERYING_OWNERSHIP,
            CLAIMING_OWNERSHIP,
            HAVING_OWNERSHIP
        }

        State _state = State.NETWORK_IS_NOT_READY;
        BasisNetworkShim __networkShim;

        float _lastNetworkUpdateTime = 0;
        int _lastNetworkUpdateFrame = 0;

        bool _valueMessageIsCached = false;
        byte[] _cachedValueMessage = null;

        bool _initialValueIsSet = false;
        byte[] _serializedInitialValue = null;

        float _ownershipAcquringStartAt = 0;

        bool _readyIsNotFired = true;

        CancelSignal _taskCancelSignal;

        public virtual void Start()
        {
            __networkShim = SafeUtil.MakeNetworkable(this);

            __networkShim.NetworkMessageReceived += OnNetworkMessageReceived;
            __networkShim.OwnershipTransfer += OnOwnershipTransfer;
            __networkShim.ServerOwnershipDestroyed += OnOwnershipDestroyed;

            __networkShim.NetworkReady += OnNetworkReady;

            Initialize();
        }

        public virtual void OnEnable()
        {
            Initialize();
        }

        public virtual void OnDisable()
        {
            Deinitialize();
        }

        void Initialize()
        {
            // Debug.Log("[SimpleVariableSync] Initializing.");

            TryGetSerializedInitialValue(out byte[] _);

            // We don't know what is happened before the initialization.
            // Assume that there is a owner.
            MarkAsOwnerExists();

            // _readyIsFired shouldn't be cleared

            _taskCancelSignal?.Cancel();

            _taskCancelSignal = new CancelSignal();

            // NOTE: Re-initialize can cause duplicated task chain, but I don't care it for now
            RepublishTask(_taskCancelSignal);
            OwnerCheckTask(_taskCancelSignal);
        }

        void Deinitialize()
        {
            if (_state != State.NETWORK_IS_NOT_READY)
            {
                SetState(State.WAITING_FOR_INITIAL_PUBLISH);
            }
        }

        protected override void SyncToOthers()
        {
            _valueMessageIsCached = false;

            if (_state != State.HAVING_OWNERSHIP)
            {
                ClaimOwnership();
            }
            else
            {
                Publish();
            }
        }

        protected override void SendCustomNetworkMessage(byte[] message, DeliveryMethod deliveryMethod, ushort[] playerIds)
        {
            __networkShim.SendCustomNetworkEvent(WrapToNetworkMessage(NMSG_CUSTOM_EVENT, message), deliveryMethod, playerIds);
        }

        public override string ToString()
        {
            return $"{_state}, lastnetworkupdate: {_lastNetworkUpdateTime}, current: {Time.time}";
        }

        void OnNetworkReady()
        {
            MarkAsOwnerExists();

            // NOTE: First TransferOwnership fires before NetworkReady.
            if (_state == State.NETWORK_IS_NOT_READY)
            {
                SetState(State.WAITING_FOR_INITIAL_PUBLISH);
            }
        }

        void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            _valueMessageIsCached = false;
            MarkAsOwnerExists();

            if (__networkShim.IsOwnedLocallyOnServer)
            {
                SetState(State.HAVING_OWNERSHIP);
                Publish();
            }
            else
            {
                SetState(State.WAITING_FOR_INITIAL_PUBLISH);
            }
        }

        void OnOwnershipDestroyed()
        {
            RecoveryOwnership();
        }

        void RepublishTask(CancelSignal _cancelSignal)
        {
            if (_cancelSignal.Cancelled) return;

            try
            {
                if (_state == State.HAVING_OWNERSHIP)
                {
                    Publish();
                }
            }
            finally
            {
                // Schedule next execution with random delay so 
                __networkShim.SendCustomEventDelayedSeconds(() => RepublishTask(_cancelSignal), REPUBLISH_INTERVAL_SEC + UnityEngine.Random.Range(0f, 0.1f));
            }
        }

        void OwnerCheckTask(CancelSignal _cancelSignal)
        {
            if (_cancelSignal.Cancelled) return;

            try
            {
                CheckOwnership();
            }
            finally
            {
                __networkShim.SendCustomEventDelayedSeconds(() => OwnerCheckTask(_cancelSignal), OWNER_CHECK_INTERVAL_SEC + UnityEngine.Random.Range(0f, 0.1f));
            }
        }

        void Publish()
        {
            if (!__networkShim.HasNetworkID)
            {
                Debug.LogWarning($"[ContinuousVariableSync] OwnershipTransfer occured but no network id. Skipping publication.");
                return;
            }

            if (!TryGetMessageCache(out byte[] message))
            {
                // Failed to get message
                Debug.LogError("[ContinuousVariableSync] Failed to create value message. Skipping publication.");
                return;
            }

            // We republish values constantly so order and reliability isn't really important
            __networkShim.SendCustomNetworkEvent(message, DeliveryMethod.Unreliable);
        }

        void OnNetworkMessageReceived(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            var stream = new MemoryStream(buffer);
            var reader = new BinaryReader(stream);

            switch (reader.ReadByte())
            {
                case NMSG_VALUE:
                    ValueReceived(playerId, reader);
                    break;
                case NMSG_CUSTOM_EVENT:
                    OnCustomNetworkMessageReceived(playerId, UnwrapToPayload(buffer), deliveryMethod);
                    break;
            }
        }

        void ValueReceived(ushort playerId, BinaryReader reader)
        {
            switch (_state)
            {
                case State.CLAIMING_OWNERSHIP:
                    Debug.Log($"[ContinuousVariableSync] Value received while local player acquiring ownership. Skipping.");
                    break;
                case State.HAVING_OWNERSHIP:
                    Debug.LogWarning($"[ContinuousVariableSync] Value received while local player owns object ownership. Skipping the value and checking ownership.");
                    CheckOwnership();
                    break;
                default:
                    if (playerId != __networkShim.CurrentOwnerId)
                    {
                        Debug.LogWarning($"[ContinuousVariableSync] Value received from non-owner {playerId} but current known owner is {__networkShim.CurrentOwnerId}. Skipping.");
                        break;
                    }
                    if (!DeserializeAndApply(reader))
                    {
                        // Failed to deserialize
                        Debug.LogError($"[ContinuousVariableSync] Failed to decerialize received buffer. Skipping.");
                        break;
                    }

                    SetState(State.SYNCED);

                    MarkAsOwnerExists();
                    break;
            }
        }

        void CheckOwnership()
        {
            if (Time.time > _lastNetworkUpdateTime + VALUE_FALLBACK_SEC && Time.frameCount > _lastNetworkUpdateFrame + VALUE_FALLBACK_FRAME && _state != State.HAVING_OWNERSHIP && _state != State.WAITING_FOR_INITIAL_PUBLISH)
            {
                // NOTE: This happens when no one has basis.ownership.transfer.
                Debug.LogWarning($"[ContinuousVariableSync] No one is publishing value in past value fallback threshold. Fall backing to Initial state.");

                if (TryGetSerializedInitialValue(out byte[] serializedInitialValue))
                {
                    DeserializeAndApply(new BinaryReader(new MemoryStream(serializedInitialValue)));
                }
                else
                {
                    Debug.LogError($"[ContinuousVariableSync] Failed to get serialized inital value. Fallbacking is failed and we may diverged.");
                }

                SetState(State.WAITING_FOR_INITIAL_PUBLISH);
            }

            switch (_state)
            {
                case State.SYNCED:
                case State.WAITING_FOR_INITIAL_PUBLISH:
                    if (OwnerIsGone())
                    {
                        RecoveryOwnership();
                    }
                    break;
                case State.CLAIMING_OWNERSHIP:
                    if (Time.time > _ownershipAcquringStartAt + OWNERSHIP_ACQUIRE_TIMEOUT_SEC)
                    {
                        // Ownership acquire timeout...
                        Debug.LogError($"[ContinuousVariableSync] Failed to acquiring ownership. Packet dropped? Retrying.");
                        RecoveryOwnership();
                    }
                    break;
                case State.HAVING_OWNERSHIP:
                    if (!__networkShim.IsOwnedLocallyOnServer)
                    {
                        Debug.LogWarning($"[ContinuousVariableSync] State is PUBLISHING but local player does not have ownership. Recoverying ownership.");
                        RecoveryOwnership();
                        return;
                    }
                    break;
            }
        }

        void RecoveryOwnership()
        {
            SetState(State.RECOVERYING_OWNERSHIP);

            // FIXME: Wait in CheckOwnership()? Using SendCustomDelayedEvents
            // outside the normal may induce lifecycle bugs...
            __networkShim.SendCustomEventDelayedSeconds(() =>
            {
                if (_state == State.RECOVERYING_OWNERSHIP && OwnerIsGone())
                {
                    // Still there is no owner. Take ownership.
                    ClaimOwnership();
                }
            }, UnityEngine.Random.Range(REPUBLISH_INTERVAL_SEC, OWNERSHIP_ELECTION_DELAY_MAX));
        }

        void ClaimOwnership()
        {
            if (!__networkShim.HasNetworkID)
            {
                Debug.LogWarning("[ContinuousVariableSync] Tried to claim ownership but network is not ready. Skipping.");
                return;
            }

            _valueMessageIsCached = false;
            SetState(State.CLAIMING_OWNERSHIP);
            _ownershipAcquringStartAt = Time.time;
            __networkShim.TakeOwnership();

            Publish();

            // Then OwnershipTransfer occurs or timeout in OwnerCheckTask
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void MarkAsOwnerExists()
        {
            _lastNetworkUpdateTime = Time.time;
            _lastNetworkUpdateFrame = Time.frameCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool OwnerIsGone()
        {
            if (_state == State.WAITING_FOR_INITIAL_PUBLISH)
            {
                return Time.time > _lastNetworkUpdateTime + UNINITIALIZED_OWNERSHIP_INVALIDATION_SEC && Time.frameCount > _lastNetworkUpdateFrame + UNINITIALIZED_OWNERSHIP_INVALIDATION_FRAME;
            }

            return Time.time > _lastNetworkUpdateTime + OWNERSHIP_INVALIDATION_SEC && Time.frameCount > _lastNetworkUpdateFrame + OWNERSHIP_INVALIDATION_FRAME;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SetState(State newState)
        {
            // Debug.Log($"[SimpleVariableSynced] State is set to: {newState}");

            _state = newState;

            if (_readyIsNotFired && (newState == State.SYNCED || newState == State.HAVING_OWNERSHIP))
            {
                _readyIsNotFired = false;
                Ready();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryGetSerializedInitialValue(out byte[] serializedInitialValue)
        {
            if (!_initialValueIsSet)
            {
                var memoryStream = new MemoryStream();
                var writer = new BinaryWriter(memoryStream);

                if (!Serialize(writer))
                {
                    Debug.LogError($"[ContinuousVariableSync] Failed to serialize initial value.");
                    serializedInitialValue = null;
                    return false;
                }

                writer.Flush();

                _serializedInitialValue = memoryStream.ToArray();
                _initialValueIsSet = true;
            }

            serializedInitialValue = _serializedInitialValue;
            return true;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryGetMessageCache(out byte[] messageCache)
        {
            if (!_valueMessageIsCached)
            {
                var stream = new MemoryStream();
                var writer = new BinaryWriter(stream);

                writer.Write(NMSG_VALUE);

                if (!Serialize(writer))
                {
                    Debug.LogError("[ContinuousVariableSync] Failed to serialize current value.");
                    messageCache = null;
                    return false;
                }

                stream.Flush();

                _cachedValueMessage = stream.ToArray();
                _valueMessageIsCached = true;
            }

            messageCache = _cachedValueMessage;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte[] WrapToNetworkMessage(byte header, byte[] serialized)
        {
            var message = new byte[1 + serialized.Length];

            message[0] = header;

            Array.Copy(serialized, 0, message, 1, serialized.Length);

            return message;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte[] UnwrapToPayload(byte[] message)
        {
            var serialized = new byte[message.Length - 1];

            Array.Copy(message, 1, serialized, 0, serialized.Length);

            return serialized;
        }
    }
}
