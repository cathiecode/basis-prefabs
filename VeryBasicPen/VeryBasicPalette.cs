using UnityEngine;
using UnityEngine.UI;

namespace SuperNekoya.VeryBasicPen
{
    [Cilboxable]
    public class VeryBasicPalette : MonoBehaviour
    {
        [SerializeField] VeryBasicPen _pen;
        [SerializeField] Button _button;
        [SerializeField] Color _color;

        void Start()
        {
            if (TryGetComponent<Button>(out var _button))
            {
                _button.onClick.AddListener(OnClick);
            }
            else
            {
                Debug.LogWarning("[VeryBasicPalette] Failed to get Button");
            }
        }

        void OnClick()
        {
            _pen.ChangeColor(_color);
        }
    }
}