using System;
using System.Collections.Generic;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

namespace com.superneko.bcutils
{
    /*
    /// <summary>
    /// HistoryVariableSync is a relatively simple manualsync-style variable
    /// synchronization algorithm. It requires you to SyncToOthers() to
    /// synchronize any variables touched.
    /// </summary>
    public abstract class HistoryVariableSync : VariableSyncBase
    {
        enum State
        {
            NETWORK_NOT_READY,
            UNINITIALIZED,
            SYNCED,
            STALLING,
            PUBLISHING
        }

        const byte NMT_REQUEST_CURRENT_STATE = 0; // Payload: message type(1 byte)
        const byte NMT_UPDATE_STATE = 1; // Payload: message type(1 byte) + serialized variable(SerializedSize byte)
        const byte NMT_PROPOSE_STATE_CHANGE = 2; // Payload: message type(1 byte) + serialized variable(SerializedSize byte)
        const byte NMT_CUSTOM_NETWORK_EVENT = 3; // Payload: message type(1byte) + custom event(any byte)

        protected abstract bool AllowAnyoneToProposeUpdate { get; }

        State _state = State.NETWORK_NOT_READY;
        BasisNetworkShim _networkShim;

        Dictionary<ushort, (float, byte[])> _queuedUpdates = new();

        public virtual void Start()
        {
            _networkShim = SafeUtil.MakeNetworkable(this);
            _networkShim.NetworkReady += OnNetworkReady;
            _networkShim.NetworkMessageReceived += OnNetworkMessageReceived;
            _networkShim.OwnershipTransfer += OnOwnershipTransfer;
            _networkShim.ServerOwnershipDestroyed += OnOwnershipDestroyed;
        }

        protected sealed override void SyncToOthers()
        {
            ProposeVariableChange();
        }

        protected sealed override void SendCustomNetworkMessage(byte[] message, DeliveryMethod deliveryMethod, ushort[] playerIds)
        {
            if (_state == State.NETWORK_NOT_READY)
            {
                Debug.LogWarning("[HistoryVariableSynced] Tried to send custom network event but network is not ready yet.");
                return;
            }
            _networkShim.SendCustomNetworkEvent(WrapToNetworkMessage(NMT_CUSTOM_NETWORK_EVENT, message), deliveryMethod, playerIds);
        }

        void OnNetworkReady()
        {
            if (_networkShim.IsOwnedLocallyOnServer)
            {
                SetState(State.PUBLISHING);
                Ready();
            }
            else
            {
                _state = State.UNINITIALIZED;
                InvalidateLocalVariable(true);
            }
        }

        void OnNetworkMessageReceived(ushort sender, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer.Length == 0)
            {
                Debug.LogError("[HistoryVariableSync] Empty message received. Skipping.");
            }

            switch (buffer[0])
            {
                case NMT_REQUEST_CURRENT_STATE:
                    SendVariableAsOwner(new ushort[] { sender });
                    break;

                case NMT_PROPOSE_STATE_CHANGE:
                    UpdateVariableAsOwner(buffer);
                    break;

                case NMT_UPDATE_STATE:
                    UpdateVariableAsFollower(sender, buffer);
                    break;

                case NMT_CUSTOM_NETWORK_EVENT:
                    OnCustomNetworkMessageReceived(sender, UnwrapToPayload(buffer), deliveryMethod);
                    break;
            }
        }

        void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            if (_networkShim.IsOwnedLocallyOnServer)
            {
                SendVariableAsOwner();
                if (_state == State.UNINITIALIZED)
                {
                    _state = State.SYNCED;
                    // Ready();
                }
                else
                {
                    _state = State.SYNCED;
                }
            }
            else
            {
                if (_queuedUpdates.TryGetValue(newOwner.playerId, out var timeAndMessage))
                {
                    var (time, message) = timeAndMessage;
                    _queuedUpdates.Remove(newOwner.playerId);

                    UpdateVariableAsFollower(newOwner.playerId, message);

                    if (Time.time - time < 10)
                    {
                        Debug.Log($"[HistoryVariableSync] Safety fallback worked! Ownership replication has done after owner's update. New owner is {newOwner.playerId}");
                    }
                    else
                    {
                        Debug.Log($"[HistoryVariableSync] Safety fallback application was skipped because the latest owner's value was too old. New owner is {newOwner.playerId}.");
                    }
                }
            }
        }

        void OnOwnershipDestroyed()
        {
            ScheduleOwnerElection();
        }

        int _electionCount = 0;

        void ScheduleOwnerElection()
        {
            var currentElectionCount = ++_electionCount;

            var delay = 0f;

            switch (_state)
            {
                case State.SYNCED:
                    delay = UnityEngine.Random.Range(0f, 1f);
                    break;
                case State.STALLING:
                    delay = UnityEngine.Random.Range(2f, 3f);
                    break;
                case State.UNINITIALIZED:
                    delay = UnityEngine.Random.Range(4f, 5f);
                    break;
                default:
                    Debug.LogError("[HistoryVariableSync] Received ownership destroy but network state was:" + _state);
                    return;
            }

            _networkShim.SendCustomEventDelayedSeconds(() =>
            {
                if (currentElectionCount != _electionCount)
                {
                    // Outdated call. return.
                    return;
                }

                _networkShim.RequestOwnershipIfNone();
            }, delay);
        }

        void ProposeVariableChange()
        {
            if (_state == State.NETWORK_NOT_READY)
            {
                Debug.LogWarning("[HistoryVariableSync] Tried to propose update state but network was not ready");
                return;
            }

            if (_networkShim.IsOwnedLocallyOnServer)
            {
                SendVariableAsOwner();
                return;
            }

            if (!AllowAnyoneToProposeUpdate)
            {
                Debug.LogWarning("[HistoryVariableSync] Non-owner can't propose update. Requesting current value to owner to revert changes.");

                InvalidateLocalVariable(true);

                return;
            }

            if (!Serialize(out var serialized))
            {
                Debug.LogWarning("[HistoryVariableSync] Failed to serialize variables. Skipping.");
                return;
            }

            var message = WrapToNetworkMessage(NMT_PROPOSE_STATE_CHANGE, serialized);

            var owner = _networkShim.CurrentOwnerId;

            _networkShim.SendCustomNetworkEvent(message, DeliveryMethod.ReliableOrdered, new ushort[] { owner });

            InvalidateLocalVariable(false); // Waits for response of propose
        }

        void UpdateVariableAsOwner(byte[] message)
        {
            if (!_networkShim.IsOwnedLocallyOnServer)
            {
                Debug.LogWarning("[HistoryVariableSync] Tried to update value directly but I was not the owner.");
                return;
            }

            if (!AllowAnyoneToProposeUpdate)
            {
                Debug.LogWarning("[HistoryVariableSync] Received proposal from non-owner. Skipping.");
                return;
            }

            if (!DeserializeAndApply(UnwrapToPayload(message)))
            {
                Debug.LogError("[HistoryVariableSync] Failed to deserialize current state. Skipping publication.");
                return;
            }

            SendVariableAsOwner();
        }

        void UpdateVariableAsFollower(ushort sender, byte[] message)
        {
            if (_networkShim.CurrentOwnerId != sender)
            {
                // NOTE: Safety fallback
                // Custom network event and ownership transfer event is sent
                // separately in each channels. So it can happen:
                // 1. Alice is left
                // 2. Bob received Alice's left event
                // 3. Bob takes ownership
                // 4. Bob sends his local values
                // 5. Carol receives Bob's values
                // 6. Carol receives Alice's left event
                Debug.Log($"[HistoryVariableSync] Safety fallback started: variable update was sent by non-owner {sender} instead of known current owner {_networkShim.CurrentOwnerId}. Let's give it a try.");
                _queuedUpdates[sender] = (Time.time, message);
                return;
            }

            if (!DeserializeAndApply(UnwrapToPayload(message)))
            {
                Debug.LogError("[HistoryVariableSync] Failed to apply received owner state.");
                return;
            }

            if (_state == State.UNINITIALIZED)
            {
                _state = State.SYNCED;
                // Ready();
            }
            else
            {
                _state = State.SYNCED;
            }

        }

        void SendVariableAsOwner(ushort[] playerIds = null)
        {
            if (_state == State.NETWORK_NOT_READY)
            {
                Debug.LogWarning("[HistoryVariableSync] Tried to send current state but network was not ready.");
                return;
            }

            if (!_networkShim.IsOwnedLocallyOnServer)
            {
                Debug.LogWarning("[HistoryVariableSync] Tried to send current state but I was not the owner.");
                return;
            }

            if (!Serialize(out var serialized))
            {
                Debug.LogError("[HistoryVariableSync] Failed to serialize variables. Skipping.");
                return;
            }

            var message = WrapToNetworkMessage(NMT_UPDATE_STATE, serialized);

            _networkShim.SendCustomNetworkEvent(message, DeliveryMethod.ReliableOrdered, playerIds);
        }

        void SetState(State state)
        {
            _state = state;
        }

        byte[] WrapToNetworkMessage(byte header, byte[] serialized)
        {
            var message = new byte[1 + serialized.Length];

            message[0] = header;

            Array.Copy(serialized, 0, message, 1, serialized.Length);

            return message;
        }

        byte[] UnwrapToPayload(byte[] message)
        {
            var serialized = new byte[message.Length - 1];

            Array.Copy(message, 1, serialized, 0, serialized.Length);

            return serialized;
        }

        void InvalidateLocalVariable(bool requestImmediately)
        {
            if (_state == State.NETWORK_NOT_READY)
            {
                Debug.LogWarning("[HistoryVariableSync] Tried to invalidate state state but network was not ready");
                return;
            }

            if (_networkShim.IsOwnedLocallyOnServer)
            {
                // I'm the owner. Owner's value is always considered as valid.
                if (_state == State.UNINITIALIZED)
                {
                    _state = State.SYNCED;
                    // Ready();
                }
                else
                {
                    _state = State.SYNCED;
                }

                return;
            }

            if (_state == State.SYNCED)
            {
                _state = State.STALLING;
            }

            if (requestImmediately)
            {
                _networkShim.SendCustomNetworkEvent(new byte[] { NMT_REQUEST_CURRENT_STATE }, DeliveryMethod.ReliableOrdered, new ushort[] { _networkShim.CurrentOwnerId });
            }

            _networkShim.SendCustomEventDelayedSeconds(() =>
            {
                if (_state != State.SYNCED)
                {
                    // Owner is not responding

                    // Meaning:
                    // when requestImmediate = false: Owner didn't send update in wait window so we need to request it explicitly
                    // when requestImmediate = true: Owner didn't respond to request so we're retrying
                    InvalidateLocalVariable(true);
                }
            }, 1f);
        }
    }
    */
}
