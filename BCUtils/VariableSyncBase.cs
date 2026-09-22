using System;
using System.IO;
using Basis.Network.Core;
using UnityEngine;

namespace com.superneko.bcutils
{
    [Cilboxable]
    public abstract class VariableSyncBase : MonoBehaviour
    {
        // Ready is called after initial synchronization(late-joiner sync) performed.
        protected virtual void Ready() { }

        protected virtual void SyncToOthers()
        {
            throw new Exception("Variable sync provider did'nt provide this method");
        }

        protected virtual bool Serialize(BinaryWriter binaryWriter)
        {
            throw new Exception("Variable sync provider did'nt provide this method");
        }
        protected virtual bool DeserializeAndApply(BinaryReader binaryReader)
        {
            throw new Exception("Variable sync provider did'nt provide this method");
        }

        protected virtual void SendCustomNetworkMessage(byte[] message, DeliveryMethod deliveryMethod, ushort[] playerIds)
        {
            throw new Exception("Variable sync provider did'nt provide this method");
        }
        protected virtual void OnCustomNetworkMessageReceived(ushort sender, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            throw new Exception("Variable sync provider did'nt provide this method");
        }

        public override string ToString() { return "VariableSyncBase"; }
    }
}
