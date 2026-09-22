using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using UnityEngine;

namespace SuperNekoya.VeryBasicPen
{
    [Cilboxable]
    public class VeryBasicEraser : MonoBehaviour
    {
        enum Mode
        {
            PerStroke = 0,
            Partial = 1
        }

        [SerializeField] BasisPickupInteractable _pickup;
        [SerializeField] SphereCollider _selfSphereCollider;
        [SerializeField] VeryBasicPen[] _pens;
        [SerializeField] Mode _mode;

        HashSet<ulong> _dirtyStrokes;

        // FIXME: Keying by string is unreliable a bit. I wish someday BasisCilbox exposes EntityId type.
        Dictionary<string, LineRenderer> _nearbyStrokes;
        Vector3[] _buffer;

        public bool Disabled = false;

        void Start()
        {
            _nearbyStrokes = new Dictionary<string, LineRenderer>();
            _dirtyStrokes = new HashSet<ulong>();
            _buffer = new Vector3[1024];

            _pickup.OnPickupUse.AddListener(OnPickupUse);
        }

        void OnDestroy()
        {
            _pickup.OnPickupUse.RemoveListener(OnPickupUse);
        }

        void OnPickupUse(BasisPickUpUseMode pickUpUseMode)
        {
            switch (pickUpUseMode)
            {
                case BasisPickUpUseMode.OnPickUpUseUp:
                    {
                        foreach (var dirtyStroke in _dirtyStrokes)
                        {
                            foreach (var pen in _pens)
                            {
                                pen.StrokeUpdatedExternally(dirtyStroke, false);
                            }
                        }

                        _dirtyStrokes.Clear();
                        break;
                    }
                case BasisPickUpUseMode.OnPickUpStillDown:
                case BasisPickUpUseMode.OnPickUpUseDown:
                    {
                        switch (_mode)
                        {
                            case Mode.PerStroke:
                                EraseColliding();
                                break;
                            case Mode.Partial:
                                PartiallyEraseColliding();
                                break;
                        }
                        break;
                    }
            }
        }

        void OnTriggerEnter(Collider other)
        {
            if (other == null) return; // FIXME: Is it really happens?

            var go = other.gameObject;

            if (_nearbyStrokes.ContainsKey(go.name)) return;

            var triggeredColliderName = go.name;

            if (!triggeredColliderName.StartsWith("Stroke_"))
            {
                return;
            }

            if (!ulong.TryParse(triggeredColliderName[7..], out var id)) /* "Stroke_".Length = 7 */
            {
                return;
            }

            if (!other.TryGetComponent<LineRenderer>(out var lineRenderer))
            {
                return;
            }

            var belongsToRegisteredPen = false;

            foreach (var pen in _pens)
            {
                if (pen.IsStrokeBelongsToThis(id))
                {
                    belongsToRegisteredPen = true;
                    break;
                }
            }

            if (!belongsToRegisteredPen)
            {
                return;
            }

            _nearbyStrokes.Add(other.gameObject.name, lineRenderer);
        }

        void OnTriggerExit(Collider other)
        {
            if (other == null) return;

            _nearbyStrokes.Remove(other.gameObject.name);
        }

        void EraseColliding()
        {
            if (Disabled) return;

            // FIXME: Using lossyScale.x
            var eraserRadius = _selfSphereCollider.radius * _selfSphereCollider.transform.lossyScale.x;
            var eraserRadiusSq = eraserRadius * eraserRadius;
            var eraserPosition = _selfSphereCollider.transform.position;

            var eraserPositionX = eraserPosition.x;
            var eraserPositionY = eraserPosition.y;
            var eraserPositionZ = eraserPosition.z;

            // Somehow this helps about 4ms for each 1024 segments
            var buffer = _buffer;

            var _strokeToRemove = new HashSet<string>();

            foreach (var stroke in _nearbyStrokes)
            {
                var goName = stroke.Key;
                var lineRenderer = stroke.Value;

                if (lineRenderer == null)
                {
                    _strokeToRemove.Add(goName);
                    continue;
                }

                if (!ulong.TryParse(goName[7..], out var id)) /* "Stroke_".Length = 7 */
                {
                    Debug.LogWarning("[VeryBasicEraser] Invalid stroke. Logic error.");
                    return;
                }

                // I'm believing this is the fastest way...
                var length = lineRenderer.GetPositions(buffer);

                var hit = false;

                for (int i = 0; i < length; i++)
                {
                    var pointX = buffer[i].x;
                    var pointY = buffer[i].y;
                    var pointZ = buffer[i].z;

                    var distSq =
                        (eraserPositionX - pointX) * (eraserPositionX - pointX) +
                        (eraserPositionY - pointY) * (eraserPositionY - pointY) +
                        (eraserPositionZ - pointZ) * (eraserPositionZ - pointZ);

                    if (distSq < eraserRadiusSq)
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                {
                    continue;
                }

                Debug.Log($"[VeryBasicEraser] Found stroke: {goName}");

                foreach (var pen in _pens)
                {
                    if (pen == null) continue;

                    pen.EraseStroke(id);

                    _strokeToRemove.Add(goName);
                }
            }

            foreach (var stroke in _strokeToRemove)
            {
                _nearbyStrokes.Remove(stroke);
            }
        }

        // NOTE: Copy and paste is fastest
        void PartiallyEraseColliding()
        {
            if (Disabled) return;

            // FIXME: Using lossyScale.x
            var eraserRadius = _selfSphereCollider.radius * _selfSphereCollider.transform.lossyScale.x;
            var eraserRadiusSq = eraserRadius * eraserRadius;
            var eraserPosition = _selfSphereCollider.transform.position;

            var eraserPositionX = eraserPosition.x;
            var eraserPositionY = eraserPosition.y;
            var eraserPositionZ = eraserPosition.z;

            // Somehow this helps about 4ms for each 1024 segments
            var buffer = _buffer;

            // NOTE: Unsupported native constructor call on exisiting instance!
            // new Vector3(-100000f, 0f, 0f);
            var erasedPosition = Vector3.zero;
            erasedPosition.x = -100000f;

            var _strokeToRemove = new HashSet<string>();

            foreach (var stroke in _nearbyStrokes)
            {
                var goName = stroke.Key;
                var lineRenderer = stroke.Value;

                if (lineRenderer == null)
                {
                    _strokeToRemove.Add(goName);
                    continue;
                }

                if (!ulong.TryParse(goName[7..], out var id)) /* "Stroke_".Length = 7 */
                {
                    Debug.LogWarning("[VeryBasicEraser] Invalid stroke. Logic error.");
                    return;
                }

                // I'm believing this is the fastest way...
                var length = lineRenderer.GetPositions(buffer);

                var erasedEverything = true;
                var erasedSomething = false;

                for (int i = 0; i < length; i++)
                {
                    var pointX = buffer[i].x;
                    var pointY = buffer[i].y;
                    var pointZ = buffer[i].z;

                    if (pointX < -99999f)
                    {
                        continue;
                    }

                    var distSq =
                        (eraserPositionX - pointX) * (eraserPositionX - pointX) +
                        (eraserPositionY - pointY) * (eraserPositionY - pointY) +
                        (eraserPositionZ - pointZ) * (eraserPositionZ - pointZ);

                    if (distSq < eraserRadiusSq)
                    {
                        lineRenderer.SetPosition(i, erasedPosition);
                        erasedSomething = true;
                    }
                    else
                    {
                        erasedEverything = false;
                    }
                }

                Debug.Log($"[VeryBasicEraser] Found stroke: {goName}");

                if (erasedEverything)
                {
                    foreach (var pen in _pens)
                    {
                        if (pen == null) continue;

                        pen.EraseStroke(id);

                        _strokeToRemove.Add(goName);
                        _dirtyStrokes.Remove(id);
                    }
                }
                else if (erasedSomething)
                {
                    foreach (var pen in _pens)
                    {
                        if (pen == null) continue;

                        pen.StrokeUpdatedExternally(id, true);
                        _dirtyStrokes.Add(id);
                    }
                }
            }

            foreach (var stroke in _strokeToRemove)
            {
                _nearbyStrokes.Remove(stroke);
            }
        }
    }
}
