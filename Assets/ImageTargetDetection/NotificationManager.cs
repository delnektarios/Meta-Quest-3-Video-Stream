using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// NotificationManager (v2)
///
/// Shows notification bubbles at the BOTTOM of the user's view.
/// Bubbles stay visible after the sign is lost — only dismissed
/// when the user taps them or taps X.
///
/// Fixes vs v1:
///   - Bubbles positioned in camera-space (always bottom of view)
///   - Bubbles survive sign loss (don't despawn automatically)
///   - Icon texture properly applied
///   - Uses OVRRaycaster-compatible canvas for button interaction
/// </summary>
public class NotificationManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Prefab")]
    [Tooltip("The notification bubble prefab.")]
    public GameObject NotificationBubblePrefab;

    [Header("Content Viewer")]
    [Tooltip("The ContentViewer to open when a bubble is tapped.")]
    public ContentViewer ContentViewer;

    [Header("Camera")]
    [Tooltip("CenterEyeAnchor camera.")]
    public Camera XRCamera;

    [Header("Position Settings")]
    [Tooltip("Vertical offset above the sign center (meters).")]
    [Range(0f, 0.5f)]
    public float BubbleVerticalOffset = 0.2f;

    [Tooltip("Horizontal spacing between multiple bubbles (meters).")]
    [Range(0.05f, 0.5f)]
    public float HorizontalSpacing = 0.25f;

    [Header("Animation")]
    public float FadeDuration = 0.25f;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    // Active bubbles keyed by target ID
    private Dictionary<string, NotificationBubble> _activeBubbles = new();
    private Dictionary<string, TargetData> _activeTargetData = new();

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (XRCamera == null)
            XRCamera = Camera.main;
    }

    private void Update()
    {
        // Bubbles fixed in world space — only rotate to face user
        FaceUserUpdate();
    }

    // -------------------------------------------------------------------------
    // Public API — called by MultiTargetDetector
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called every detection frame for each detected target.
    /// Spawns bubble on first detection, ignores subsequent calls
    /// (bubble stays until user dismisses it).
    /// </summary>
    // Store sign world positions for bubble placement
    private Dictionary<string, Vector3> _signWorldPositions = new();

    public void OnTargetDetected(TargetData data, Vector3 worldCenter,
                                  Quaternion worldRotation, Vector3[] worldCorners)
    {
        // Already have a bubble for this target — do nothing
        if (_activeBubbles.ContainsKey(data.id))
            return;
        if (_activeTargetData.ContainsKey(data.id))
            return;

        // Store sign world position for bubble placement
        _signWorldPositions[data.id] = worldCenter;
        _activeTargetData[data.id] = data;

        // Spawn bubble at sign position
        SpawnBubble(data, worldCenter);
    }

    /// <summary>
    /// Called when a target is lost from view.
    /// We intentionally do NOTHING here — bubble stays until user dismisses.
    /// </summary>
    public void OnTargetLost(string targetId)
    {
        // Intentionally empty — bubble persists after sign is lost
        Debug.Log($"[NotificationManager] Target lost: {targetId} — bubble stays.");
    }

    // -------------------------------------------------------------------------
    // Bubble Management
    // -------------------------------------------------------------------------

    private void SpawnBubble(TargetData data, Vector3 signWorldCenter)
    {
        if (NotificationBubblePrefab == null)
        {
            Debug.LogError("[NotificationManager] NotificationBubblePrefab not assigned!");
            return;
        }

        // Place bubble slightly above the sign center, facing the user
        Vector3 spawnPos = signWorldCenter + Vector3.up * BubbleVerticalOffset;

        // Offset horizontally if multiple bubbles
        if (_activeBubbles.Count > 0)
            spawnPos += XRCamera.transform.right * (_activeBubbles.Count * HorizontalSpacing);

        // Face the user at spawn time
        Vector3 toUser = (XRCamera.transform.position - spawnPos).normalized;
        toUser.y = 0;
        Quaternion spawnRot = toUser != Vector3.zero
            ? Quaternion.LookRotation(-toUser, Vector3.up)
            : Quaternion.identity;

        GameObject bubbleGo = Instantiate(
            NotificationBubblePrefab,
            spawnPos,
            spawnRot,
            transform
        );

        NotificationBubble bubble = bubbleGo.GetComponent<NotificationBubble>();
        if (bubble == null)
        {
            Debug.LogError("[NotificationManager] NotificationBubblePrefab missing NotificationBubble component!");
            Destroy(bubbleGo);
            return;
        }

        // Setup bubble content
        bubble.Setup(data, () => OnBubbleTapped(data));

        // Set icon from loaded target texture if available
        SetBubbleIcon(bubble, data);

        // Fade in
        StartCoroutine(FadeIn(bubble));

        _activeBubbles[data.id] = bubble;
        Debug.Log($"[NotificationManager] Bubble spawned for: {data.name}");

        // No reposition needed - bubbles are fixed at sign position
    }

    private void SetBubbleIcon(NotificationBubble bubble, TargetData data)
    {
        // Find the loaded target texture from MultiTargetDetector
        var detector = FindObjectOfType<MultiTargetDetector>();
        if (detector == null) return;

        foreach (var loadedTarget in detector.LoadedTargets)
        {
            if (loadedTarget.Data.id == data.id && loadedTarget.Texture != null)
            {
                bubble.SetIcon(loadedTarget.Texture);
                return;
            }
        }
    }

    private void OnBubbleTapped(TargetData data)
    {
        Debug.Log($"[NotificationManager] Bubble tapped: {data.name}");

        // Open content viewer
        if (ContentViewer != null)
            ContentViewer.Show(data);

        // Dismiss the bubble
        DismissBubble(data.id);
    }

    public void DismissBubble(string targetId)
    {
        if (!_activeBubbles.ContainsKey(targetId)) return;

        var bubble = _activeBubbles[targetId];
        _activeBubbles.Remove(targetId);
        _activeTargetData.Remove(targetId);

        StartCoroutine(FadeAndDestroy(bubble));
        RepositionAllBubbles();
    }

    // -------------------------------------------------------------------------
    // Position — Fixed at Sign, Always Facing User
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rotate all active bubbles to always face the user.
    /// Position stays fixed at the sign location.
    /// </summary>
    private void FaceUserUpdate()
    {
        if (_activeBubbles.Count == 0 || XRCamera == null) return;

        foreach (var kvp in _activeBubbles)
        {
            var bubble = kvp.Value;
            if (bubble == null) continue;

            // Rotate to face user (billboard around Y axis only)
            Vector3 toUser = XRCamera.transform.position - bubble.transform.position;
            toUser.y = 0;
            if (toUser != Vector3.zero)
            {
                Quaternion targetRot = Quaternion.LookRotation(-toUser, Vector3.up);
                bubble.transform.rotation = Quaternion.Lerp(
                    bubble.transform.rotation,
                    targetRot,
                    Time.deltaTime * 5f
                );
            }
        }
    }

    // Keep for compatibility — no longer repositions
    private void RepositionAllBubbles()
    {
        int i = 0;
        int total = _activeBubbles.Count;
        foreach (var kvp in _activeBubbles)
        {
            if (kvp.Value == null) continue;
            // Bubbles stay at their sign position — no reposition
            i++;
            i++;
        }
    }

    // -------------------------------------------------------------------------
    // Fade
    // -------------------------------------------------------------------------

    private IEnumerator FadeIn(NotificationBubble bubble)
    {
        if (bubble == null) yield break;
        CanvasGroup cg = bubble.GetComponent<CanvasGroup>();
        if (cg == null) yield break;

        float elapsed = 0f;
        cg.alpha = 0f;
        while (elapsed < FadeDuration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = Mathf.Lerp(0f, 1f, elapsed / FadeDuration);
            yield return null;
        }
        cg.alpha = 1f;
    }

    private IEnumerator FadeAndDestroy(NotificationBubble bubble)
    {
        if (bubble == null) yield break;

        CanvasGroup cg = bubble.GetComponent<CanvasGroup>();
        if (cg != null)
        {
            float elapsed = 0f;
            float startAlpha = cg.alpha;
            while (elapsed < FadeDuration)
            {
                elapsed += Time.deltaTime;
                cg.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / FadeDuration);
                yield return null;
            }
        }

        Destroy(bubble.gameObject);
    }
}
