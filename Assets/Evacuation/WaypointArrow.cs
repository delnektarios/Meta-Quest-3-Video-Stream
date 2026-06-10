using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// WaypointArrow
///
/// Component on each floor arrow prefab.
/// Handles:
///   - Visual setup (color, pulse animation)
///   - Distance label
///   - Active/inactive state (next arrow vs future arrows)
///
/// PREFAB SETUP:
///   Create a simple arrow shape:
///   - GameObject "WaypointArrow"
///     ├── ArrowMesh (3D arrow pointing forward along Z axis)
///     │   └── MeshRenderer with green unlit material
///     ├── DistanceLabel (World Space TextMeshPro)
///     └── WaypointArrow script (this)
///
/// QUICK PREFAB (use primitive):
///   - Create a Cylinder, scale to (0.1, 0.02, 0.3)
///   - Create a Cube as arrow head, scale to (0.2, 0.02, 0.2)
///   - Position head at Z=0.2
///   - Rotate head 45° on Y
///   - Both use green unlit material
/// </summary>
public class WaypointArrow : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Visual")]
    public Renderer ArrowRenderer;
    public TextMeshPro DistanceLabel;

    [Header("Colors")]
    public Color ActiveColor = new Color(0.2f, 0.9f, 0.3f, 1f);    // bright green
    public Color InactiveColor = new Color(0.1f, 0.5f, 0.15f, 0.7f); // dim green

    [Header("Animation")]
    [Tooltip("Pulse the active arrow to draw attention.")]
    public bool PulseWhenActive = true;
    public float PulseSpeed = 2f;
    public float PulseMinScale = 0.8f;
    public float PulseMaxScale = 1.1f;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private bool _isActive = false;
    private Vector3 _baseScale;
    private MaterialPropertyBlock _mpb;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        _baseScale = transform.localScale;
        _mpb = new MaterialPropertyBlock();
    }

    private void Update()
    {
        if (_isActive && PulseWhenActive)
        {
            float pulse = Mathf.Lerp(PulseMinScale, PulseMaxScale,
                (Mathf.Sin(Time.time * PulseSpeed) + 1f) / 2f);
            transform.localScale = _baseScale * pulse;
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Setup this arrow with waypoint data.
    /// </summary>
    public void SetupArrow(int waypointNumber, float distanceFromOrigin, bool isNext)
    {
        _isActive = isNext;

        // Set color
        Color color = isNext ? ActiveColor : InactiveColor;
        if (ArrowRenderer != null)
        {
            ArrowRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_Color", color);
            ArrowRenderer.SetPropertyBlock(_mpb);
        }

        // Set distance label
        if (DistanceLabel != null)
        {
            DistanceLabel.text = isNext
                ? $"→ {distanceFromOrigin:F0}m"
                : $"{waypointNumber}";
            DistanceLabel.color = isNext ? Color.white : new Color(1,1,1,0.5f);
        }

        // Reset scale
        transform.localScale = _baseScale;
    }

    /// <summary>
    /// Animate arrow fading out when waypoint is reached.
    /// </summary>
    public void AnimateReached()
    {
        StartCoroutine(FadeOut());
    }

    private IEnumerator FadeOut()
    {
        float elapsed = 0f;
        float duration = 0.5f;
        Vector3 startScale = transform.localScale;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, t);
            yield return null;
        }

        Destroy(gameObject);
    }
}
