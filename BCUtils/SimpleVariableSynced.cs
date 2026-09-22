using System;
using System.IO;
using System.Runtime.CompilerServices;
using Basis;
using Basis.Network.Core;
using UnityEngine;

namespace com.superneko.bcutils
{
    /// <summary>
    /// Simpliest and lightweight yet unreliable variable sync. No convergence
    /// guarantee. No behaviour activeness safety. Suitable for tiny toy prop.
    /// Only responsibility for the owner is late-joiner sync.
    /// - Complexity: Extremely low
    /// - Bandwidth: Extremely low
    /// - Reliability: Low
    /// </summary>
    public abstract class SimpleVariableSync : VariableSyncBase
    {
        const byte NMSG_VALUE = 0;
        const byte NMSG_VALUE_REQUEST = 1;
        const byte NMSG_CUSTOM_EVENT = 2;

        bool __networkIsReady = false;
        bool _valueIsReady = false;

        BasisNetworkShim __networkShim;

        public virtual void Start()
        {
            __networkShim = SafeUtil.MakeNetworkable(this);
            __networkShim.NetworkReady += OnNetworkReady;
            __networkShim.NetworkMessageReceived += OnNetworkMessageReceived;
            __networkShim.ServerOwnershipDestroyed += OnServerOwnershipDestroyed;
        }

        void OnNetworkReady()
        {
            __networkIsReady = true;

            if (__networkShim.IsOwnedLocallyOnServer)
            {
                _valueIsReady = true;
                Ready();
            }

            ScheduleRequestLateJoinerSync();
        }

        void OnNetworkMessageReceived(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer == null)
            {
                // FIXME: I'm unsure about this, but it seems sometimes happens. Why?
                Debug.LogError($"[SimpleVariableSync] Null message received. Why???");
                return;
            }

            if (buffer.Length == 0)
            {
                Debug.LogError($"[SimpleVariableSync] Empty message received. Skipping.");
                return;
            }

            // NOTE: Both of MemoryStream and BinaryReader implements IDisposable, 
            // but according to the documentation, we don't need to dispose it.
            var memoryStream = new MemoryStream(buffer);
            var reader = new BinaryReader(memoryStream);

            switch (reader.ReadByte())
            {
                case NMSG_VALUE:
                    if (!DeserializeAndApply(reader))
                    {
                        Debug.LogError($"[SimpleVariableSync] Failed to deserialize payload.");
                        return;
                    }

                    if (!_valueIsReady)
                    {
                        _valueIsReady = true;
                        Ready();
                    }

                    return;
                case NMSG_VALUE_REQUEST:
                    SendCurrentValue(new ushort[] { playerId });
                    return;
                case NMSG_CUSTOM_EVENT:
                    OnCustomNetworkMessageReceived(playerId, UnwrapToPayload(buffer), deliveryMethod);
                    break;
            }
        }

        void OnServerOwnershipDestroyed()
        {
            if (!__networkIsReady)
            {
                Debug.LogWarning("[SimpleVariableSync] Triend to request ownership (if none) but network is not ready.");
                return;
            }
            __networkShim.SendCustomEventDelayedSeconds(__networkShim.RequestOwnershipIfNone, UnityEngine.Random.Range(0.0f, 1.0f));
        }

        public virtual void OnEnable()
        {

            ScheduleRequestLateJoinerSync();
        }

        protected override void SendCustomNetworkMessage(byte[] message, DeliveryMethod deliveryMethod, ushort[] playerIds)
        {
            if (!__networkIsReady)
            {
                Debug.LogError("[SimpleVariableSync] Tried to send custom network message but network is not ready.");
                return;
            }

            __networkShim.SendCustomNetworkEvent(WrapToNetworkMessage(NMSG_CUSTOM_EVENT, message), deliveryMethod, playerIds);
        }

        protected override void SyncToOthers()
        {
            SendCurrentValue();
        }

        void ScheduleRequestLateJoinerSync()
        {
            if (!__networkIsReady)
            {
                Debug.LogWarning("[SimpleVariableSync] Tried to schedule late joiner sync request but network is not ready.");
                return;
            }

            if (__networkShim.IsOwnedLocallyOnServer) return;

            // Ask to owner
            __networkShim.SendCustomNetworkEvent(new byte[] { NMSG_VALUE_REQUEST }, DeliveryMethod.ReliableOrdered, new ushort[] { __networkShim.CurrentOwnerId });

            __networkShim.SendCustomEventDelayedSeconds(RequestLateJoinerSyncStage1, 1f);
        }

        void RequestLateJoinerSyncStage1()
        {
            if (_valueIsReady) return;

            // Owner is not responding. Ask to everyone

            Debug.LogWarning("[SimpleVariableSync] Owner didn't respond to . Fall backing to initial value.");

            __networkShim.SendCustomNetworkEvent(new byte[] { NMSG_VALUE_REQUEST }, DeliveryMethod.ReliableOrdered);

            __networkShim.SendCustomEventDelayedSeconds(RequestLateJoinerSyncStage2, 1f);
        }

        void RequestLateJoinerSyncStage2()
        {
            if (_valueIsReady) return;

            // Still no chance. Take ownership.

            Debug.LogWarning("[SimpleVariableSync] Failed to request initial value. Fall backing to initial value.");

            __networkShim.TakeOwnership();

            _valueIsReady = true;
            Ready();
        }

        protected void SendCurrentValue(ushort[] recipients = null)
        {
            if (!__networkIsReady)
            {
                Debug.LogWarning("[SimpleVariableSync] Tried to send current value but network is not ready.");
                return;
            }

            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);

            writer.Write(NMSG_VALUE);

            if (!Serialize(writer))
            {
                Debug.LogError($"[SimpleVariableSynced] Failed to serialize {gameObject.name}");
                return;
            }

            writer.Flush();

            __networkShim.SendCustomNetworkEvent(stream.ToArray(), DeliveryMethod.ReliableOrdered, recipients);
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
