using System;
using System.Collections.Generic;
using System.IO;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking.NetworkedAvatar;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/*
    Flow of Events and Data (Summary; some exception exists)

    
    +----+    +------------+    +-----------------+    +------+
    | UI | -> | Controller | -> |      Model      | -> | View |
    +----+    +------------+    +-----------------+    +------+
                                  ^             |
                                  |             V
                          +-----------+      +-----------+
                          | Net(Recv) | <=== | Net(Send) |
                          +-----------+      +-----------+
*/

namespace SuperNekoya.VeryBasicPen
{
    // NOTE: I'm assuming several things to keep logic simple.
    // - New stroke must not be made at speeds of 5hz or higher.
    // - The receiver of the StrokePayload will not receive the Erase message during the transmission delay.
    // - Duling replying of RequestHistory, seeder will not disconnected from the session.
    // - Collision of id will never happen.
    // I want to split this code however there are tons of limitation and performance considerations. Please don't blame me.
    [Cilboxable]
    public class VeryBasicPen : MonoBehaviour
    {
        const byte NMSG_StrokePayload = 0;
        const byte NMSG_StartWriting = 1;
        const byte NMSG_FinishWriting = 2;
        const byte NMSG_EraseAll = 3;
        const byte NMSG_EraseStroke = 4;
        const byte NMSG_RequestHistory = 5;
        const byte NMSG_ColorChange = 6;
        const byte NMSG_StrokePayloadHalf = 7;

        enum Result
        {
            Ok = 0,
            EInput = -100,
            EInternal = -200,
        }

        const byte STATE_IDLE = 0;
        const byte STATE_LOCAL_WRITING = 1;
        const byte STATE_REMOTE_WRITING = 2;

        enum Tool
        {
            Pen = 0,
            Eraser = 1
        }

        struct ProcessingHistoryRequest
        {
            public int PlayerId;
            public int NextStrokeIndex;
            public ulong[] StrokeIds;

            public (int, int, ulong[]) ToTuple()
            {
                return (PlayerId, NextStrokeIndex, StrokeIds);
            }

            static public ProcessingHistoryRequest FromTuple((int, int, ulong[]) tuple)
            {
                return new ProcessingHistoryRequest
                {
                    PlayerId = tuple.Item1,
                    NextStrokeIndex = tuple.Item2,
                    StrokeIds = tuple.Item3
                };
            }
        }

        class Cancellable
        {
            public bool Cancelled { get; private set; } = false;

            public void Cancel()
            {
                Cancelled = true;
            }
        }

        const int PACKED_INT16_LENGTH = 2;
        const int PACKED_ULONG_LENGTH = 8;
        const int PACKED_FLOAT_LENGTH = 4;
        const int PACKED_VECTOR3_LENGTH = PACKED_FLOAT_LENGTH * 3;
        const int PACKED_VECTOR3INT16_LENGTH = PACKED_INT16_LENGTH * 3;

        const int NETWORK_HEADER_SIZE = 1;

        const int STROKE_ID_OFFSET = NETWORK_HEADER_SIZE;
        const int STROKE_ID_SIZE = PACKED_ULONG_LENGTH;

        const int STROKE_COLOR_OFFSET = STROKE_ID_OFFSET + STROKE_ID_SIZE;
        const int STROKE_COLOR_SIZE = PACKED_VECTOR3_LENGTH;

        const int STROKE_PAYLOAD_OFFSET = STROKE_COLOR_OFFSET + STROKE_COLOR_SIZE;

        const int STROKE_INT16_ID_OFFSET = NETWORK_HEADER_SIZE;
        const int STROKE_INT16_ID_SIZE = PACKED_ULONG_LENGTH;
        const int STROKE_INT16_COLOR_OFFSET = STROKE_INT16_ID_OFFSET + STROKE_INT16_ID_SIZE;
        const int STROKE_INT16_COLOR_SIZE = PACKED_VECTOR3_LENGTH;
        const int STROKE_INT16_OFFSET_OFFSET = STROKE_INT16_COLOR_OFFSET + STROKE_INT16_COLOR_SIZE;
        const int STROKE_INT16_OFFSET_SIZE = PACKED_VECTOR3_LENGTH;
        const int STROKE_INT16_PAYLOAD_OFFSET = STROKE_INT16_OFFSET_OFFSET + STROKE_INT16_OFFSET_SIZE;

        const int ERASESTROKE_ID_OFFSET = NETWORK_HEADER_SIZE;

        const int MAX_STROKE_LENGTH = 500;

        const float ERASED_SEGMENT_X = -100000;
        const float ERASED_SEGMENT_X_THRESHOLD = -90000;
        const int ERASED_SEGMENT_X_INT16 = -32768;

        const float MINIMUM_SEGMENT_LENGTH = 0.001f;  // 1mm

        [Header("User settings")]
        [SerializeField] Color _initialColor;
        [SerializeField] Color _pallet1;
        [SerializeField] Color _pallet2;
        [SerializeField] Color _pallet3;

        public float _clickThresholdTime = 0.25f; // 250ms
        public float _doubleClickThresholdTime = 0.3f; // 300ms
        public bool _doubleClickToolCycleEnabled = true;

        [Header("Developer settings")]
        [SerializeField] Transform _finishedStrokeRoot;
        [SerializeField] Transform tipTransform;
        [SerializeField] LineRenderer _templateStrokeLineRenderer;
        [SerializeField] LineRenderer _pendingStrokeLineRenderer;
        [SerializeField] LineRenderer _remotePendingStrokeLineRenderer;
        [SerializeField] BasisPickupInteractable _pickup;
        [SerializeField] VeryBasicEraser _penTipEraser;
        [SerializeField] GameObject _penTipEraserModel;
        [SerializeField] Renderer _colorIndicatorRenderer;

        [SerializeField] Button _initialColorButton;
        [SerializeField] Button _pallet1Button;
        [SerializeField] Button _pallet2Button;
        [SerializeField] Button _pallet3Button;
        [SerializeField] Image _initialColorImage;
        [SerializeField] Image _pallet1Image;
        [SerializeField] Image _pallet2Image;
        [SerializeField] Image _pallet3Image;
        [SerializeField] Button _eraseAllButton;
        [SerializeField] Button _doubleClickToolCycleToggleButton;
        [SerializeField] TMP_Text _doubleClickToolCycleToggleButtonText;

        private bool _initialized = false;
        private bool _networkReady;
        private Color _color = Color.white;
        private byte _state;

        private int _pendingStrokeLength;
        private Vector3[] _pendingStroke;

        private int _remotePendingStrokeLength;
        private Vector3[] _remotePendingStroke;

        private BasisNetworkShim _networkShim;

        private List<ulong> _finishedStrokeIds;

        // HACK: Cilbox currently does not allow us to use Collection of Cilbox type.
        // However, tuple types which only contains host type is also a host type.
        // So we can use it instead.
        private Dictionary<ulong, (LineRenderer, Bounds)> _finishedStrokeViews;

        private List<(int, int, ulong[])> _processingHistoryRequest;
        private Cancellable _historyRequestTask;

        private float _lastUseDownTime = 0;
        private float _lastUseUpTime = 0;
        private bool _lastUseUpWasDoubleClick = false;

        private Tool _currentTool = Tool.Pen;

        private MaterialPropertyBlock _colorIndicatorMaterialPropertyBlock;

        #region Lifecycle
        public void Start()
        {
            if (_initialized) return;
            LogDebug("Initializing VeryBasicPen");
            Controller_Init();
            Model_Init();
            Networking_Init();
            View_Init();
            _initialized = true;
        }

        public void OnDestroy()
        {
            if (!_initialized) return;
            LogDebug("Deinitializing VeryBasicPen");
            Controller_DeInit();
            Model_DeInit();
            Networking_DeInit();
            View_DeInit();
        }

        public void Update()
        {
            // NOTE: Unrolled to gain performance because Update() is hotpath.

            // ex-UpdateModel()
            switch (_state)
            {
                case STATE_REMOTE_WRITING:
                    {
                        var position = tipTransform.position;

                        if (IsImportantSegment(_remotePendingStroke, _remotePendingStrokeLength, position))
                        {
                            if (_remotePendingStrokeLength < MAX_STROKE_LENGTH)
                            {
                                _remotePendingStroke[_remotePendingStrokeLength++] = position;

                                // ex-View_RemoteStrokeSegmentAdded()
                                var index = _remotePendingStrokeLineRenderer.positionCount++;
                                _remotePendingStrokeLineRenderer.SetPosition(index, position);
                            }
                        }
                    }
                    break;
                case STATE_LOCAL_WRITING:
                    {
                        var position = tipTransform.position;

                        if (IsImportantSegment(_pendingStroke, _pendingStrokeLength, position))
                        {
                            if (_pendingStrokeLength >= MAX_STROKE_LENGTH)
                            {
                                FinishLocalWritingRequested();
                                StartLocalWritingRequested();
                            }

                            _pendingStroke[_pendingStrokeLength++] = position;

                            // ex-View_LocalStrokeSegmentAdded()
                            var index = _pendingStrokeLineRenderer.positionCount++;
                            _pendingStrokeLineRenderer.SetPosition(index, position);
                        }
                    }

                    break;
            }
        }

        #endregion

        #region Controller

        public void Controller_Init()
        {
            _pickup.OnPickupUse.AddListener(OnPickupUse);
            _pickup.OnInteractEndEvent.AddListener(OnInteractEnd);
            _eraseAllButton.onClick.AddListener(OnEraseAllClicked);
            _doubleClickToolCycleToggleButton.onClick.AddListener(OnDoubleClickCycleToggleClicked);
            _initialColorButton.onClick.AddListener(OnInitialColorButtonClicked);
            _pallet1Button.onClick.AddListener(OnPallet1ButtonClicked);
            _pallet2Button.onClick.AddListener(OnPallet2ButtonClicked);
            _pallet3Button.onClick.AddListener(OnPallet3ButtonClicked);

            _initialColorImage.color = _initialColor;
            _pallet1Image.color = _pallet1;
            _pallet2Image.color = _pallet2;
            _pallet3Image.color = _pallet3;
        }

        public void Controller_DeInit()
        {
            _pickup.OnPickupUse.RemoveListener(OnPickupUse);
            _eraseAllButton.onClick.RemoveListener(OnEraseAllClicked);
        }

        public void EraseStroke(ulong id)
        {
            EraseStrokeRequestedFromLocal(id);
        }

        public void ChangeColor(Color color)
        {
            ColorChangeRequestedFromLocal(color);
        }

        public void StrokeUpdatedExternally(ulong id, bool isTransient)
        {
            StrokeUpdatedExternallyInLocal(id, isTransient);
        }

        public bool IsStrokeBelongsToThis(ulong id)
        {
            return _finishedStrokeIds.Contains(id);
        }

        void OnPickupUse(BasisPickUpUseMode pickupUseMode)
        {
            if (pickupUseMode == BasisPickUpUseMode.OnPickUpStillDown)
            {
                // I don't care
                return;
            }

            bool isClickLike = false;
            bool isDoubleClick = false;

            if (pickupUseMode == BasisPickUpUseMode.OnPickUpUseUp)
            {
                if (Time.time - _lastUseUpTime < _clickThresholdTime)
                {
                    isClickLike = true;
                }

                if (isClickLike && !_lastUseUpWasDoubleClick && Time.time - _lastUseUpTime < _doubleClickThresholdTime)
                {
                    isDoubleClick = true;
                    _lastUseUpWasDoubleClick = true;
                }
                else
                {
                    _lastUseUpWasDoubleClick = false;
                }
            }

            switch (_currentTool)
            {
                case Tool.Pen:
                    if (isDoubleClick && _doubleClickToolCycleEnabled)
                    {
                        CycleTool();

                        CancelLocalWritingRequested();
                    }
                    else if (pickupUseMode == BasisPickUpUseMode.OnPickUpUseDown)
                    {
                        StartLocalWritingRequested();
                    }
                    else if (pickupUseMode == BasisPickUpUseMode.OnPickUpUseUp)
                    {
                        FinishLocalWritingRequested();
                    }
                    break;
                case Tool.Eraser:
                    if (isDoubleClick && _doubleClickToolCycleEnabled)
                    {
                        CycleTool();
                    }
                    break;
            }

            switch (pickupUseMode)
            {
                case BasisPickUpUseMode.OnPickUpUseUp:
                    _lastUseUpTime = Time.time;
                    break;
                case BasisPickUpUseMode.OnPickUpUseDown:
                    _lastUseDownTime = Time.time;
                    break;
            }
        }

        void OnInteractEnd(BasisInput basisInput)
        {
            SetPenTool();
        }

        void CycleTool()
        {
            switch (_currentTool)
            {
                case Tool.Pen:
                    SetEraserTool();
                    break;
                case Tool.Eraser:
                    SetPenTool();
                    break;
            }
        }

        void SetPenTool()
        {
            _currentTool = Tool.Pen;
            _penTipEraser.Disabled = true;
            _penTipEraserModel.SetActive(false);
        }

        void SetEraserTool()
        {
            _currentTool = Tool.Eraser;
            _penTipEraser.Disabled = false;
            _penTipEraserModel.SetActive(true);
        }

        void OnEraseAllClicked()
        {
            EraseAllRequestedRequestedFromLocal();
        }

        void OnDoubleClickCycleToggleClicked()
        {
            _doubleClickToolCycleEnabled = !_doubleClickToolCycleEnabled;
            _doubleClickToolCycleToggleButtonText.text = _doubleClickToolCycleEnabled ? "Double click\nEnabled" : "Double click\nDisabled";
        }

        void OnInitialColorButtonClicked()
        {
            ColorChangeRequestedFromLocal(_initialColor);
        }

        void OnPallet1ButtonClicked()
        {
            ColorChangeRequestedFromLocal(_pallet1);
        }

        void OnPallet2ButtonClicked()
        {
            ColorChangeRequestedFromLocal(_pallet2);
        }

        void OnPallet3ButtonClicked()
        {
            ColorChangeRequestedFromLocal(_pallet3);
        }

        #endregion

        #region Model

        void Model_Init()
        {
            _color = _initialColor;
            _pendingStrokeLength = 0;
            _pendingStroke = new Vector3[MAX_STROKE_LENGTH];

            _remotePendingStrokeLength = 0;
            _remotePendingStroke = new Vector3[MAX_STROKE_LENGTH];

            _finishedStrokeIds = new List<ulong>();
        }

        void Model_DeInit()
        {
            // There's nothing to do
        }

        void StartLocalWritingRequested()
        {
            _state = STATE_LOCAL_WRITING;

            _pendingStrokeLength = 0;
            _pendingStroke = new Vector3[MAX_STROKE_LENGTH];

            View_LocalWritingStarted();
            Networking_LocalWritingStarted();
        }

        void FinishLocalWritingRequested()
        {
            _state = STATE_IDLE;

            ulong id = GenerateLexSortId();

            if (!AddFinishedStroke(_pendingStroke, _pendingStrokeLength, id, _color, out var bounds))
            {
                LogWarning("Finish local writing failed");
            }

            View_LocalWritingFinished();

            Networking_LocalWritingFinished(id, _pendingStroke, _pendingStrokeLength, _color, bounds);

            // Prepare line renderer for next stroke
            _pendingStrokeLength = 0;
            _pendingStroke = new Vector3[MAX_STROKE_LENGTH];
        }

        void CancelLocalWritingRequested()
        {
            _state = STATE_IDLE;

            View_LocalWritingCancelled();
            Networking_LocalWritingCancelled();

            _pendingStrokeLength = 0;
            _pendingStroke = new Vector3[MAX_STROKE_LENGTH];
        }

        void RemoteWritingStarted()
        {

            _state = STATE_REMOTE_WRITING;
            _remotePendingStrokeLength = 0;

            View_RemoteWritingStarted();
        }

        void RemoteWritingFinished()
        {
            _state = STATE_IDLE;
            _remotePendingStrokeLength = 0;

            View_RemoteWritingFinished();
        }

        void RemoteWritingArrived(Vector3[] strokeData, int length, ulong id, Color color)
        {
            if (_finishedStrokeIds.Contains(id))
            {
                UpdateFinishedStroke(strokeData, length, id, color, out var _);
            }
            else
            {
                AddFinishedStroke(strokeData, length, id, color, out var _);
            }
        }

        void EraseAllRequestedRequestedFromLocal()
        {
            EraseAllRequested();

            Networking_EraseAllRequestedFromLocal();
        }

        void EraseStrokeRequestedFromLocal(ulong strokeId)
        {
            if (!_finishedStrokeIds.Contains(strokeId))
            {
                return;
            }

            EraseStrokeRequested(strokeId);

            Networking_Erased(strokeId);
        }

        void EraseStrokeRequested(ulong strokeId)
        {
            if (!_finishedStrokeIds.Contains(strokeId))
            {
                return;
            }

            _finishedStrokeIds.Remove(strokeId);

            LogDebug($"Erased Stroke {strokeId}");

            View_Erased(strokeId);
        }

        void EraseAllRequested()
        {
            _finishedStrokeIds = new List<ulong>();

            View_AllErased();
        }

        void ColorChangeRequestedFromLocal(Color color)
        {
            _color = color;

            View_ColorChanged(color);
            Networking_ColorChanged(color);
        }

        void RemoteChangedColor(Color color)
        {
            _color = color;
            View_ColorChanged(color);
        }

        void StrokeUpdatedExternallyInLocal(ulong strokeId, bool isTransient)
        {
            // FIXME: Send transient update at low frequency
            if (isTransient)
            {
                return;
            }

            if (!_finishedStrokeIds.Contains(strokeId))
            {
                return;
            }

            // FIXME: Allocation
            var positions = new Vector3[MAX_STROKE_LENGTH];
            var positionCount = 0;
            var color = new Color();
            var bounds = new Bounds();

            Repository_GetFinishedLineRendererValue(strokeId, ref positions, ref positionCount, ref color, ref bounds);

            Networking_StrokeChangedExternally(strokeId, positions, positionCount, color, bounds, isTransient);
        }

        bool AddFinishedStroke(Vector3[] strokeData, int length, ulong strokeId, Color color, out Bounds bounds)
        {
            // HACK: Compiler emits 0x10 when we assign to argument directly,
            // however Cilbox does not have implementation for that for now (2026/06/06)
            var actualLength = length >= 0 ? length : strokeData.Length;

            if (actualLength == 0)
            {
                bounds = new Bounds();
                return false;
            }

            if (_finishedStrokeIds.Contains(strokeId))
            {
                bounds = new Bounds();
                return false;
            }

            var positions = new Vector3[actualLength];
            Array.Copy(strokeData, positions, actualLength);

            _finishedStrokeIds.Add(strokeId);

            View_FinishedStrokeAdded(strokeId, positions, color, out bounds);

            return true;
        }

        bool UpdateFinishedStroke(Vector3[] strokeData, int length, ulong strokeId, Color color, out Bounds bounds)
        {
            var actualLength = length >= 0 ? length : strokeData.Length;

            if (!_finishedStrokeIds.Contains(strokeId))
            {
                LogWarning("Tried to update strokes that does not exists.");
                bounds = new Bounds();
                return false;
            }

            var positions = new Vector3[actualLength];
            Array.Copy(strokeData, positions, actualLength);

            View_FinishedStrokeUpdated(strokeId, positions, color, out bounds);

            return true;
        }

        static bool IsImportantSegment(Vector3[] history, int historyLength, Vector3 current)
        {
            const float MINIMUM_SEGMENT_LENGTH_SQ = MINIMUM_SEGMENT_LENGTH * MINIMUM_SEGMENT_LENGTH;
            if (historyLength < 3) return true;

            // TODO: Make threshold distance configurable
            var lastX = history[historyLength - 1].x;
            var lastY = history[historyLength - 1].y;
            var lastZ = history[historyLength - 1].z;

            var currentX = current.x;
            var currentY = current.y;
            var currentZ = current.z;

            var distSq =
                (lastX - currentX) * (lastX - currentX) +
                (lastY - currentY) * (lastY - currentY) +
                (lastZ - currentZ) * (lastZ - currentZ);

            if (distSq > MINIMUM_SEGMENT_LENGTH_SQ) return true;

            // TODO: Angle-based importance detection?

            return false;
        }

        #endregion

        #region Networking(Etc)

        void Networking_Init()
        {
            _networkShim = SafeUtil.MakeNetworkable(this);

            if (_networkShim != null)
            {
                _processingHistoryRequest = new();
                _networkShim.NetworkReady += OnNetworkReady;
                _networkShim.NetworkMessageReceived += OnNetworkMessageReceived;
                _networkShim.ServerOwnershipDestroyed += OnPlayerLeft;
                _networkShim.OwnershipTransfer += OnOwnershipTransfer;
            }
            else
            {
                LogDebug("Cannot find network shim. Content police may removed that. Working local only.");
            }
        }

        void Networking_DeInit()
        {
            if (_networkShim != null)
            {
                _networkShim.NetworkReady -= OnNetworkReady;
                _networkShim.NetworkMessageReceived -= OnNetworkMessageReceived;
                _networkShim.ServerOwnershipDestroyed -= OnPlayerLeft;
                _networkShim.OwnershipTransfer -= OnOwnershipTransfer;
            }
        }

        void OnNetworkReady()
        {
            LogDebug("Network is ready.");

            _networkReady = true;

            Networking_NetworkReady();
        }

        void OnPlayerLeft()
        {
            _networkShim.RequestOwnershipIfNone();
        }

        void OnOwnershipTransfer(BasisNetworkPlayer _)
        {
            RefreshHistoryRequestTask();
        }

        void RefreshHistoryRequestTask()
        {
            _historyRequestTask?.Cancel();

            if (_networkShim.IsOwnedLocallyOnServer)
            {
                _historyRequestTask = new Cancellable();
                StartProcessHistoryRequestTask(_historyRequestTask);
            }
        }

        void StartProcessHistoryRequestTask(Cancellable cancel)
        {
            if (cancel.Cancelled) return;

            ProcessHistoryRequest();

            _networkShim.SendCustomEventDelayedSeconds(() =>
            {
                StartProcessHistoryRequestTask(cancel);
            }, 1f);
        }

        void ScheduleProcessHistoryRequest(int playerId)
        {
            _processingHistoryRequest.Add(new ProcessingHistoryRequest { PlayerId = playerId, NextStrokeIndex = 0, StrokeIds = _finishedStrokeIds.ToArray() }.ToTuple());
        }

        #endregion

        #region Networking(Recv)

        void OnNetworkMessageReceived(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            LogDebug($"Network message received({buffer.Length} bytes) from playerId {playerId}");

            if (buffer.Length == 0)
            {
                LogWarning("Received empty packet.");
                return;
            }

            switch (buffer[0])
            {
                case NMSG_StrokePayload:
                    {
                        using var readStream = new MemoryStream(buffer);
                        using var reader = new BinaryReader(readStream);

                        reader.ReadByte(); // Skip network header

                        var receivedSegmentLength = (buffer.Length - STROKE_PAYLOAD_OFFSET) / PACKED_VECTOR3_LENGTH;

                        var id = reader.ReadUInt64();

                        var colorR = reader.ReadSingle();
                        var colorG = reader.ReadSingle();
                        var colorB = reader.ReadSingle();

                        var segmentBuffer = new Vector3[receivedSegmentLength];

                        for (int i = 0; i < receivedSegmentLength; i++)
                        {
                            var x = reader.ReadSingle();
                            var y = reader.ReadSingle();
                            var z = reader.ReadSingle();

                            // NOTE: My first attempt was something like this:
                            // segmentBuffer[i].x = x;
                            // segmentBuffer[i].y = y;
                            // segmentBuffer[i].z = z;
                            // This is fine in regular Mono, however, in Cilbox, it fails.
                            // The root cause is newarr <etype> of valuetype is filled with null,
                            // not a default value.
                            segmentBuffer[i] = new Vector3(x, y, z);
                        }

                        RemoteWritingArrived(segmentBuffer, -1, id, new Color(colorR, colorG, colorB));

                        break;
                    }
                case NMSG_StartWriting:
                    {
                        RemoteWritingStarted();
                        break;
                    }
                case NMSG_FinishWriting:
                    {
                        RemoteWritingFinished();

                        break;
                    }
                case NMSG_EraseAll:
                    {
                        EraseAllRequested();

                        break;
                    }
                case NMSG_EraseStroke:
                    {
                        var id = BitConverter.ToUInt64(buffer, ERASESTROKE_ID_OFFSET);

                        LogDebug($"Remote stroke erase {id}");

                        EraseStrokeRequested(id);

                        break;
                    }
                case NMSG_RequestHistory:
                    {
                        ScheduleProcessHistoryRequest(playerId);

                        break;
                    }
                case NMSG_ColorChange:
                    {
                        if (UnpackVector3(buffer, NETWORK_HEADER_SIZE, PACKED_VECTOR3_LENGTH, out var colorAsVector3) < 0)
                        {
                            LogError("Failed to receive color.");
                            return;
                        }

                        RemoteChangedColor(new Color(colorAsVector3.x, colorAsVector3.y, colorAsVector3.z));

                        break;
                    }
                case NMSG_StrokePayloadHalf:
                    {
                        using var readStream = new MemoryStream(buffer);
                        using var reader = new BinaryReader(readStream);

                        reader.ReadByte(); // Skip network header

                        var receivedSegmentLength = (buffer.Length - STROKE_INT16_PAYLOAD_OFFSET) / PACKED_VECTOR3INT16_LENGTH;

                        var id = reader.ReadUInt64();

                        var colorR = reader.ReadSingle();
                        var colorG = reader.ReadSingle();
                        var colorB = reader.ReadSingle();

                        var offsetX = reader.ReadSingle();
                        var offsetY = reader.ReadSingle();
                        var offsetZ = reader.ReadSingle();

                        var segmentBuffer = new Vector3[receivedSegmentLength];

                        for (int i = 0; i < receivedSegmentLength; i++)
                        {
                            var rawX = reader.ReadInt16();

                            var x = rawX == ERASED_SEGMENT_X_INT16 ? ERASED_SEGMENT_X : (rawX / 65534f) + offsetX;
                            var y = (reader.ReadInt16() / 65534f) + offsetY;
                            var z = (reader.ReadInt16() / 65534f) + offsetZ;

                            // NOTE: My first attempt was something like this:
                            // segmentBuffer[i].x = x;
                            // segmentBuffer[i].y = y;
                            // segmentBuffer[i].z = z;
                            // This is fine in regular Mono, however, in Cilbox, it fails.
                            // The root cause is newarr <etype> of valuetype is filled with null,
                            // not a default value.
                            segmentBuffer[i] = new Vector3(x, y, z);
                        }

                        RemoteWritingArrived(segmentBuffer, -1, id, new Color(colorR, colorG, colorB));

                        break;
                    }
                default:
                    {
                        LogWarning($"Unsupported message type: {buffer[0]}");
                        break;
                    }
            }
        }

        #endregion

        #region Networking(Send)

        void Networking_NetworkReady()
        {
            if (_finishedStrokeIds.Count != 0)
            {
                // Local player wrote something before the networking gets initialized
                ScheduleProcessHistoryRequest(-1);
            }

            // FIXME: If the owner was the local player, we fail to receive history.
            // Is it really happens?
            if (!_networkShim.IsOwnedLocallyOnServer)
            {
                _networkShim.SendCustomNetworkEvent(new byte[] { NMSG_RequestHistory }, DeliveryMethod.ReliableUnordered, new ushort[] { _networkShim.CurrentOwnerId });
            }
            else
            {
                LogWarning("RequestHistory has failed because current owner was local player.");
            }

            RefreshHistoryRequestTask();
        }

        void Networking_LocalWritingStarted()
        {
            if (!_networkReady) return;

            _networkShim.SendCustomNetworkEvent(new byte[] { NMSG_StartWriting }, DeliveryMethod.ReliableUnordered);
        }

        void Networking_LocalWritingFinished(ulong id, Vector3[] array, int length, Color color, Bounds bounds)
        {
            if (!_networkReady) return;

            SendStroke(id, array, length, -1, color, bounds);

            _networkShim.SendCustomNetworkEvent(new byte[] { NMSG_FinishWriting }, DeliveryMethod.ReliableUnordered);
        }

        void Networking_LocalWritingCancelled()
        {
            if (!_networkReady) return;

            _networkShim.SendCustomNetworkEvent(new byte[] { NMSG_FinishWriting }, DeliveryMethod.ReliableUnordered);
        }

        void Networking_EraseAllRequestedFromLocal()
        {
            if (!_networkReady) return;

            _networkShim.SendCustomNetworkEvent(new byte[] { NMSG_EraseAll }, DeliveryMethod.ReliableUnordered);
        }

        void Networking_Erased(ulong strokeId)
        {
            if (!_networkReady) return;

            var id = BitConverter.GetBytes(strokeId);

            var message = new byte[NETWORK_HEADER_SIZE + STROKE_ID_SIZE];
            message[0] = NMSG_EraseStroke;
            Array.Copy(id, 0, message, ERASESTROKE_ID_OFFSET, STROKE_ID_SIZE);

            _networkShim.SendCustomNetworkEvent(message, DeliveryMethod.ReliableUnordered);
        }

        void Networking_ColorChanged(Color color)
        {
            if (!_networkReady) return;

            SendColorChange(color, -1);
        }

        void Networking_StrokeChangedExternally(ulong id, Vector3[] array, int length, Color color, Bounds bounds, bool isTransient)
        {
            if (!_networkReady) return;

            // TODO: We should send transient updates at low frequency...
            if (!isTransient)
            {
                SendStroke(id, array, length, -1, color, bounds);
            }
        }

        void ProcessHistoryRequest()
        {
            if (_processingHistoryRequest.Count == 0 || !_networkReady)
            {
                return;
            }

            // for simplicity
            var requestIndexToProcess = UnityEngine.Random.Range(0, _processingHistoryRequest.Count);

            var request = ProcessingHistoryRequest.FromTuple(_processingHistoryRequest[requestIndexToProcess]);

            LogDebug($"Sending History to {request.PlayerId}");

            while (true)
            {
                var strokeIndexToSend = request.NextStrokeIndex++;
                _processingHistoryRequest[requestIndexToProcess] = request.ToTuple(); // increment write back

                var playerId = request.PlayerId;
                var strokeIds = request.StrokeIds;
                var strokeIdsLength = strokeIds.Length;

                // Send current color on start
                if (strokeIndexToSend == 0)
                {
                    SendColorChange(_color, playerId);
                }

                if (strokeIndexToSend < strokeIdsLength)
                {
                    var strokeId = strokeIds[strokeIndexToSend];

                    // FIXME: Allocation
                    var positions = new Vector3[MAX_STROKE_LENGTH];
                    var positionCount = 0;
                    var color = new Color();
                    var bounds = new Bounds();

                    Repository_GetFinishedLineRendererValue(strokeId, ref positions, ref positionCount, ref color, ref bounds);

                    SendStroke(strokeId, positions, positionCount, playerId, color, bounds);

                    break;
                }
                else
                {
                    _processingHistoryRequest.RemoveAt(requestIndexToProcess);
                    break;
                }
            }
        }

        void SendStroke(ulong id, Vector3[] array, int length, int recipient, Color color, Bounds bounds)
        {
            var recipients = recipient < 0 ? null : new ushort[1] { (ushort)recipient };

            var boundsSize = bounds.size;

            if (boundsSize.x < 1f && boundsSize.y < 1f && boundsSize.z < 1f)
            {
                LogDebug($"Sending stroke (id: {id}) to {recipient} (int16)");

                var boundsCenter = bounds.center;

                SendStrokeInt16(id, boundsCenter, array, length, recipients, color);
            }
            else
            {
                LogDebug($"Sending stroke (id: {id}) to {recipient} (float32)");
                SendStrokeFloat32(id, array, length, recipients, color);
            }
        }

        void SendStrokeFloat32(ulong id, Vector3[] array, int length, ushort[] recipients, Color color)
        {
            // NOTE: Basis can handle large chunk of data (at least >12KiB) so we doesn't need to split it to multiple packets... what a powerful framework!
            using var outputStream = new MemoryStream(NETWORK_HEADER_SIZE + STROKE_ID_SIZE + STROKE_COLOR_SIZE + length * PACKED_VECTOR3_LENGTH);

            using var outputWriter = new BinaryWriter(outputStream);

            // Header
            outputWriter.Write(NMSG_StrokePayload);

            // id
            outputWriter.Write(id);

            // color
            outputWriter.Write(color.r);
            outputWriter.Write(color.g);
            outputWriter.Write(color.b);

            // Payload
            for (int i = 0; i < length; i++)
            {
                outputWriter.Write(array[i].x);
                outputWriter.Write(array[i].y);
                outputWriter.Write(array[i].z);
            }

            outputWriter.Flush();
            outputStream.Flush();

            // OPTIMIZE: We may have performance advantanges by using ReliableUnordered.
            // OPTIMIZE: Avoid ToArray() by message length-unaware format?
            _networkShim.SendCustomNetworkEvent(outputStream.ToArray(), DeliveryMethod.ReliableUnordered, recipients);
        }

        // 
        // NOTE: every component of array must be in range -0.5m ~ 0.5m. Caller responsibility.
        // -0.5m - 0.5m to -32767 ~ 32767
        void SendStrokeInt16(ulong id, Vector3 offset, Vector3[] array, int length, ushort[] recipients, Color color)
        {
            // NOTE: Basis can handle large chunk of data (at least >12KiB) so we doesn't need to split it to multiple packets... what a powerful framework!
            using var outputStream = new MemoryStream(NETWORK_HEADER_SIZE + STROKE_INT16_ID_SIZE + STROKE_INT16_COLOR_SIZE + STROKE_INT16_OFFSET_SIZE + PACKED_VECTOR3INT16_LENGTH * length);

            using var outputWriter = new BinaryWriter(outputStream);

            // Header
            outputWriter.Write(NMSG_StrokePayloadHalf);

            // id
            outputWriter.Write(id);

            // color
            outputWriter.Write(color.r);
            outputWriter.Write(color.g);
            outputWriter.Write(color.b);

            var offsetX = offset.x;
            var offsetY = offset.y;
            var offsetZ = offset.z;

            // offset
            outputWriter.Write(offsetX);
            outputWriter.Write(offsetY);
            outputWriter.Write(offsetZ);

            // Payload
            for (int i = 0; i < length; i++)
            {
                var x = array[i].x;

                outputWriter.Write((short)(x < ERASED_SEGMENT_X_THRESHOLD ? ERASED_SEGMENT_X_INT16 : Mathf.Round((x - offsetX) * 65534f)));
                outputWriter.Write((short)Mathf.Round((array[i].y - offsetY) * 65534f));
                outputWriter.Write((short)Mathf.Round((array[i].z - offsetZ) * 65534f));
            }

            outputWriter.Flush();
            outputStream.Flush();

            // OPTIMIZE: We may have performance advantanges by using ReliableUnordered.
            // OPTIMIZE: Avoid ToArray() by message length-unaware format?
            _networkShim.SendCustomNetworkEvent(outputStream.ToArray(), DeliveryMethod.ReliableUnordered, recipients);
        }

        void SendColorChange(Color color, int recipient)
        {
            var recipients = recipient < 0 ? null : new ushort[1] { (ushort)recipient };

            var message = new byte[NETWORK_HEADER_SIZE + PACKED_VECTOR3_LENGTH];
            message[0] = NMSG_ColorChange;

            PackVector3(new(color.r, color.g, color.b), message, NETWORK_HEADER_SIZE);

            LogDebug($"Sending color to {recipient}");
            _networkShim.SendCustomNetworkEvent(message, DeliveryMethod.ReliableUnordered, recipients);
        }

        static int PackVector3(Vector3 data, byte[] output, int outputOffset)
        {
            if (output.Length - outputOffset < PACKED_VECTOR3_LENGTH)
            {
                LogError("output array does not have enough length");
                return (int)Result.EInput;
            }

            var x = data.x;
            var y = data.y;
            var z = data.z;

            var xBytes = BitConverter.GetBytes(x);
            Array.Copy(xBytes, 0, output, outputOffset, PACKED_FLOAT_LENGTH);

            var yBytes = BitConverter.GetBytes(y);
            Array.Copy(yBytes, 0, output, outputOffset + PACKED_FLOAT_LENGTH, PACKED_FLOAT_LENGTH);

            var zBytes = BitConverter.GetBytes(z);
            Array.Copy(zBytes, 0, output, outputOffset + PACKED_FLOAT_LENGTH * 2, PACKED_FLOAT_LENGTH);

            return 0;
        }

        static int UnpackVector3(byte[] data, int inputOffset, int inputLength, out Vector3 output)
        {
            if (inputLength % PACKED_VECTOR3_LENGTH != 0)
            {
                LogError("input offset is not aligned to Vector3 data size");
                output = Vector3.one;
                return (int)Result.EInput;
            }

            if (data.Length - inputOffset < inputLength)
            {
                LogError("input length does not have enough length");
                output = Vector3.one;
                return (int)Result.EInput;
            }

            var element = Vector3.zero;

            element.x = BitConverter.ToSingle(data, inputOffset);
            element.y = BitConverter.ToSingle(data, inputOffset + PACKED_FLOAT_LENGTH);
            element.z = BitConverter.ToSingle(data, inputOffset + PACKED_FLOAT_LENGTH * 2);

            output = element;

            return 0;
        }

        #endregion

        #region View

        void View_Init()
        {
            var scale = transform.lossyScale;

            // NOTE: I'm assuming Prop cannot be moved after its spawn
            _finishedStrokeRoot.SetPositionAndRotation(
                Vector3.zero,
                Quaternion.identity
            );

            _finishedStrokeRoot.localScale = new Vector3(
                1f / scale.x,
                1f / scale.y,
                1f / scale.z
            );

            _pendingStrokeLineRenderer.positionCount = 0;
            _finishedStrokeViews = new();
            _colorIndicatorMaterialPropertyBlock = new MaterialPropertyBlock();

            View_ColorChanged(_color);
            // color of finished stroke will be set in-flight
        }

        void View_DeInit()
        {
            _remotePendingStrokeLineRenderer.positionCount = 0;

            foreach (var (finishedStrokeLineRenderer, _) in _finishedStrokeViews.Values)
            {
                if (finishedStrokeLineRenderer != null)
                {
                    Destroy(finishedStrokeLineRenderer.gameObject);
                }
            }
        }

        void View_LocalWritingStarted()
        {
            _pendingStrokeLineRenderer.positionCount = 0;
        }

        void View_LocalWritingFinished()
        {
            _pendingStrokeLineRenderer.positionCount = 0;
        }

        void View_LocalWritingCancelled()
        {
            _pendingStrokeLineRenderer.positionCount = 0;
        }

        void View_RemoteWritingStarted()
        {
            _remotePendingStrokeLineRenderer.positionCount = 0;
        }

        void View_RemoteWritingFinished()
        {
            _remotePendingStrokeLineRenderer.positionCount = 0;
        }

        bool View_FinishedStrokeAdded(ulong id, Vector3[] positions, Color color, out Bounds bounds)
        {
            if (positions.Length == 0)
            {
                LogWarning("Refusing to add stroke with length 0");
                bounds = new Bounds();
                return false;
            }

            var newFinishedLineRenderer = InstantiateLineRenderer();
            newFinishedLineRenderer.positionCount = positions.Length;
            newFinishedLineRenderer.SetPositions(positions);
            newFinishedLineRenderer.gameObject.name = $"Stroke_{id}";
            SetColorOfLineRenderer(newFinishedLineRenderer, color);

            // FIXME: Iterating over arary costs about 4ms for 1024 samples...
            // HACK: Compiler emits 0x71 ldobj <typeTok> for reading of argument,
            // which is not implement in Cilbox
            bounds = CalculateAABBOfVector3Array(positions);

            var boxCollider = newFinishedLineRenderer.GetComponent<BoxCollider>();
            boxCollider.center = bounds.center;
            boxCollider.size = bounds.size;

            _finishedStrokeViews.Add(id, (newFinishedLineRenderer, bounds));

            return true;
        }

        bool View_FinishedStrokeUpdated(ulong id, Vector3[] positions, Color color, out Bounds bounds)
        {
            if (positions.Length == 0)
            {
                LogWarning("Refusing to add stroke with length 0");
                bounds = new Bounds();
                return false;
            }

            if (!_finishedStrokeViews.TryGetValue(id, out var finishedStroke))
            {
                LogWarning($"Failed to update stroke id {id}. This does not exist.");
                bounds = new Bounds();
                return false;
            }

            var finishedLineRenderer = finishedStroke.Item1;

            finishedLineRenderer.positionCount = positions.Length;
            finishedLineRenderer.SetPositions(positions);
            finishedLineRenderer.gameObject.name = $"Stroke_{id}";
            SetColorOfLineRenderer(finishedLineRenderer, color);


            // HACK: Compiler emits 0x71 ldobj <typeTok> for `someObject.field = outArgument`,
            // which is not implement in Cilbox
            var bounds2 = CalculateAABBOfVector3Array(positions);

            var boxCollider = finishedLineRenderer.GetComponent<BoxCollider>();
            boxCollider.center = bounds2.center;
            boxCollider.size = bounds2.size;

            bounds = bounds2;

            return true;
        }

        void View_Erased(ulong strokeId)
        {
            if (!_finishedStrokeViews.TryGetValue(strokeId, out var finishedStroke))
            {
                LogWarning($"Failed to get LineRenderer for ${strokeId}");
                return;
            }

            var lineRenderer = finishedStroke.Item1;

            _finishedStrokeViews.Remove(strokeId);

            if (lineRenderer == null)
            {
                LogWarning($"LineRenderer ${strokeId} was null");
            }

            Destroy(lineRenderer.gameObject);
        }

        void View_AllErased()
        {
            foreach (var finishedStroke in _finishedStrokeViews.Values)
            {
                var lineRenderer = finishedStroke.Item1;

                Destroy(lineRenderer);
            }

            _finishedStrokeViews.Clear();
        }

        void View_ColorChanged(Color color)
        {
            SetColorOfLineRenderer(_pendingStrokeLineRenderer, color);
            SetColorOfLineRenderer(_remotePendingStrokeLineRenderer, color);
            _colorIndicatorRenderer.GetPropertyBlock(_colorIndicatorMaterialPropertyBlock);
            _colorIndicatorMaterialPropertyBlock.SetColor("_BaseColor", color);
            _colorIndicatorRenderer.SetPropertyBlock(_colorIndicatorMaterialPropertyBlock);
        }

        // NOTE: Currently, View holds everything about inside of strke. So I decided to merge repository into View.
        bool Repository_GetFinishedLineRendererValue(ulong strokeId, ref Vector3[] positions, ref int positionCount, ref Color color, ref Bounds bounds)
        {
            if (!_finishedStrokeViews.TryGetValue(strokeId, out var finishedStroke))
            {
                return false;
            }

            var lineRenderer = finishedStroke.Item1;
            bounds = finishedStroke.Item2;

            positionCount = lineRenderer.positionCount;

            positions = new Vector3[positionCount];
            lineRenderer.GetPositions(positions);
            color = lineRenderer.colorGradient.colorKeys[0].color;

            return true;
        }

        Bounds CalculateAABBOfVector3Array(Vector3[] positions)
        {
            var length = positions.Length;

            var xMax = Mathf.NegativeInfinity;
            var xMin = Mathf.Infinity;
            var yMax = Mathf.NegativeInfinity;
            var yMin = Mathf.Infinity;
            var zMax = Mathf.NegativeInfinity;
            var zMin = Mathf.Infinity;

            float px, py, pz;

            // Accessing point costs 6 gc allocation
            for (int i = 0; i < length; i += 2)
            {
                px = positions[i].x;
                py = positions[i].y;
                pz = positions[i].z;

                if (px < ERASED_SEGMENT_X_THRESHOLD) continue;
                if (px < xMin) xMin = px;
                if (py < yMin) yMin = py;
                if (pz < zMin) zMin = pz;

                if (px > xMax) xMax = px;
                if (py > yMax) yMax = py;
                if (pz > zMax) zMax = pz;
            }

            // last point is always important
            px = positions[length - 1].x;
            py = positions[length - 1].y;
            pz = positions[length - 1].z;

            if (px < xMin) xMin = px;
            if (py < yMin) yMin = py;
            if (pz < zMin) zMin = pz;

            if (px > xMax) xMax = px;
            if (py > yMax) yMax = py;
            if (pz > zMax) zMax = pz;

            // NOTE: ok it looks a bit like silly, but Cilbox always complaints about
            // "Unsupported constructor call on existing instance".
            // Somehow we cannot assign newly created Vector3, Bounds, etc. into local variable
            // so we need to do that this way...
            // Cilbox.CilboxInterpreterRuntimeException: Unsupported native constructor call on existing instance: UnityEngine.Bounds @ (SuperNekoya.VeryBasicPen.VeryBasicPen.View_FinishedStrokeAdded, PC: 409)
            //   at Cilbox.CilboxMethod.InterpretInner (System.ArraySegment`1[T] stackBufferIn, System.ArraySegment`1[T] parametersIn) [0x0679f] in .\Packages\com.cnlohr.cilbox\Cilbox.cs:1607 
            //   at Cilbox.CilboxMethod.InterpretInner (System.ArraySegment`1[T] stackBufferIn, System.ArraySegment`1[T] parametersIn) [0x0679f] in .\Packages\com.cnlohr.cilbox\Cilbox.cs:1607 
            //   at Cilbox.CilboxMethod.InterpretInner (System.ArraySegment`1[T] stackBufferIn, System.ArraySegment`1[T] parametersIn) [0x0679f] in .\Packages\com.cnlohr.cilbox\Cilbox.cs:1607 
            //   at Cilbox.CilboxMethod.InterpretInner (System.ArraySegment`1[T] stackBufferIn, System.ArraySegment`1[T] parametersIn) [0x0679f] in .\Packages\com.cnlohr.cilbox\Cilbox.cs:1607 
            //   at Cilbox.CilboxMethod.Interpret (Cilbox.CilboxProxy ths, System.Object[] parametersIn) [0x000c9] in .\Packages\com.cnlohr.cilbox\Cilbox.cs:196 

            var centerX = (xMax + xMin) / 2f;
            var centerY = (yMax + yMin) / 2f;
            var centerZ = (zMax + zMin) / 2f;

            var sizeX = xMax - xMin + 0.1f;
            var sizeY = yMax - yMin + 0.1f;
            var sizeZ = zMax - zMin + 0.1f;

            return new Bounds(new Vector3(centerX, centerY, centerZ), new Vector3(sizeX, sizeY, sizeZ));
        }

        LineRenderer InstantiateLineRenderer()
        {
            var newLineRenderer = Instantiate(_templateStrokeLineRenderer);

            if (newLineRenderer == null)
            {
                return null;
            }

            newLineRenderer.transform.SetParent(_finishedStrokeRoot);
            newLineRenderer.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            newLineRenderer.transform.localScale = Vector3.one;
            newLineRenderer.positionCount = 0;
            newLineRenderer.gameObject.SetActive(true);

            return newLineRenderer;
        }

        #endregion

        #region Helper

        static void LogDebug(string message)
        {
#if UNITY_EDITOR
            Debug.Log($"[VeryBasicPen] [{Time.realtimeSinceStartupAsDouble:F8}] {message}");
#endif
        }

        static void LogWarning(string message)
        {
            Debug.LogWarning($"[VeryBasicPen] [{Time.realtimeSinceStartupAsDouble:F8}] {message}");
        }

        static void LogError(string message)
        {
            Debug.LogError($"[VeryBasicPen] [{Time.realtimeSinceStartupAsDouble:F8}] {message}");
        }

        static ulong GenerateLexSortId()
        {
            // TODO: Better ID Generation
            return ((ulong)DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds) * 10000UL + (ulong)(UnityEngine.Random.Range(0, 10000) % 10000);
        }

        static void SetColorOfLineRenderer(LineRenderer lineRenderer, Color color)
        {
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
        }

        #endregion
    }
}
