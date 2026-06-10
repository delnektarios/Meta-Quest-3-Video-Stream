using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// NotificationBubble (v2)
///
/// Individual notification bubble shown at bottom of view.
/// Fixed vs v1:
///   - Icon properly set from Texture2D
///   - Button uses OVR-compatible interaction
///   - Close (X) button to dismiss without opening content
///
/// PREFAB SETUP:
///   World Space Canvas (scale 0.001, ~220x90 units)
///   ├── CanvasGroup component
///   ├── OVRRaycaster component (replaces Graphic Raycaster)  ← IMPORTANT
///   ├── Background (Image, dark rounded panel)
///   │     ├── Icon (Image, 60x60, left side)
///   │     ├── TextContainer
///   │     │     ├── NameLabel (TextMeshPro, bold)
///   │     │     └── TapHintLabel (TextMeshPro, small grey)
///   │     └── CloseButton (Button, top right, shows X)
///   └── TapButton (Button, transparent, covers whole panel)
///         → NotificationBubble script on root Canvas
/// </summary>
public class NotificationBubble : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("UI References")]
    public TextMeshProUGUI NameLabel;
    public TextMeshProUGUI TapHintLabel;
    public Image IconImage;
    public Button TapButton;       // covers whole bubble — tap to open
    public Button CloseButton;     // X button — tap to dismiss only

    [Header("Icon")]
    [Tooltip("Default icon shown when no texture is available.")]
    public Sprite DefaultIcon;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private TargetData _data;
    private Action _onTap;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (TapButton != null)
            TapButton.onClick.AddListener(OnTapped);

        if (CloseButton != null)
            CloseButton.onClick.AddListener(OnClose);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Setup the bubble with target data and tap callback.
    /// </summary>
    public void Setup(TargetData data, Action onTap)
    {
        _data = data;
        _onTap = onTap;

        if (NameLabel != null)
            NameLabel.text = data.name;

        if (TapHintLabel != null)
            TapHintLabel.text = "Tap to view";

        // Set default icon initially
        if (IconImage != null && DefaultIcon != null)
            IconImage.sprite = DefaultIcon;

        // Show content indicator badges
        UpdateContentBadges(data);
    }

    /// <summary>
    /// Set the icon image from the target's downloaded texture.
    /// Called by NotificationManager after getting texture from LoadedTarget.
    /// </summary>
    public void SetIcon(Texture2D texture)
    {
        if (IconImage == null || texture == null) return;

        // Convert Texture2D to Sprite
        Sprite sprite = Sprite.Create(
            texture,
            new UnityEngine.Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f
        );

        IconImage.sprite = sprite;
        IconImage.color = Color.white; // ensure not tinted
        Debug.Log($"[NotificationBubble] Icon set for {_data?.name}");
    }

    // -------------------------------------------------------------------------
    // Content Badges
    // -------------------------------------------------------------------------

    private void UpdateContentBadges(TargetData data)
    {
        // Build a hint showing what content is available
        if (TapHintLabel == null) return;

        string hint = "Tap to view";
        var content = data.content;
        if (content != null)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(content.text)) parts.Add("📝");
            if (!string.IsNullOrEmpty(content.video_url)) parts.Add("🎬");
            if (!string.IsNullOrEmpty(content.model_3d_url)) parts.Add("🧊");

            if (parts.Count > 0)
                hint = string.Join(" ", parts) + " · Tap to view";
        }

        TapHintLabel.text = hint;
    }

    // -------------------------------------------------------------------------
    // Button Handlers
    // -------------------------------------------------------------------------

    private void OnTapped()
    {
        Debug.Log($"[NotificationBubble] Tapped: {_data?.name}");
        _onTap?.Invoke();
    }

    private void OnClose()
    {
        Debug.Log($"[NotificationBubble] Closed: {_data?.name}");

        // Find NotificationManager and dismiss
        var manager = FindObjectOfType<NotificationManager>();
        if (manager != null && _data != null)
            manager.DismissBubble(_data.id);
        else
            Destroy(gameObject);
    }
}
