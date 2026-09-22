using System.IO;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using com.superneko.bcutils;
using UnityEngine;

namespace SuperNekoya.VeryBasicSwitch
{
    [Cilboxable]
    [RequireComponent(typeof(BasisInteractableObject))]
    // FIXME: Use ContinuousVariableSync instead (Cilbox limitation and no time to refactor the provider)
    public class VeryBasicSwitch : SimpleVariableSync
    {
        [Header("Settings")]
        public bool Synced;
        public bool On;
        public GameObject[] GameObjectsToActivate;
        public GameObject[] GameObjectsToDeactivate;

        [Header("Internals")]
        public GameObject[] GameObjectsToActivateInternal;
        public GameObject[] GameObjectsToDeactivateInternal;

        public override void Start()
        {
            if (Synced) {
                base.Start();
            }

            GetComponent<BasisInteractableObject>().OnInteractStartEvent.AddListener(OnInteractStart);

            Refresh();
        }

        public void OnInteractStart(BasisInput _)
        {
            Cycle();
        }

        public void Cycle()
        {
            Set(!On);
        }

        public void Set(bool on)
        {
            On = on;

            Refresh();

            if (Synced)
            {
                SyncToOthers();
            }
        }

        protected override bool DeserializeAndApply(BinaryReader binaryReader)
        {
            On = binaryReader.ReadBoolean();

            Refresh();

            return true;
        }

        protected override bool Serialize(BinaryWriter binaryWriter)
        {
            binaryWriter.Write(On);

            return true;
        }

        public void Refresh()
        {
            foreach (var go in GameObjectsToActivate)
            {
                if (go != null)
                {
                    go.SetActive(On);
                }
            }

            foreach (var go in GameObjectsToDeactivate)
            {
                if (go != null)
                {
                    go.SetActive(!On);
                }
            }

            foreach (var go in GameObjectsToActivateInternal)
            {
                if (go != null)
                {
                    go.SetActive(On);
                }
            }

            foreach (var go in GameObjectsToDeactivateInternal)
            {
                if (go != null)
                {
                    go.SetActive(!On);
                }
            }
        }
    }
}