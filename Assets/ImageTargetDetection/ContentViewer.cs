using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;

/// <summary>
/// ContentViewer
///
/// Full content panel shown when user taps a notification bubble.
/// Shows tabs for available content: Info, Video, 3D Model.
/// Only shows tabs for content that actually exists for that target.
///
/// SCENE SETUP:
///   World Space Canvas (scale 0.001, ~600x400 units)
///   ├── Header
///   │     ├── TitleLabel (TMP)
///   │     └── CloseButton (Button)
///   ├── TabBar
///   │     ├── InfoTabButton (Button)
///   │     ├── VideoTabButton (Button)
///   │     └── ModelTabButton (Button)
///   ├── InfoPanel
///   │     ├── DescriptionLabel (TMP, scrollable)
///   │     └── ContentTextLabel (TMP, scrollable)
///   ├── VideoPanel
///   │     └── VideoPlayer (Unity VideoPlayer component)
///   └── ModelPanel
///         └── ModelContainer (for future 3D model display)
/// </summary>
public class ContentViewer : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Root")]
    public Canvas ViewerCanvas;

    [Header("Header")]
    public TextMeshProUGUI TitleLabel;
    public Button CloseButton;

    [Header("Tab Buttons")]
    public Button InfoTabButton;
    public Button VideoTabButton;
    public Button ModelTabButton;

    [Header("Panels")]
    public GameObject InfoPanel;
    public GameObject VideoPanel;
    public GameObject ModelPanel;

    [Header("Info Panel Content")]
    public TextMeshProUGUI DescriptionLabel;
    public TextMeshProUGUI ContentTextLabel;

    [Header("Video Panel")]
    public VideoPlayer VideoPlayer;
    public RawImage VideoDisplay;

    [Header("Animation")]
    public float FadeDuration = 0.25f;

    [Header("Position")]
    [Tooltip("Distance in front of camera to show the viewer.")]
    public float ViewerDistance = 1.5f;

    [Tooltip("Vertical offset from center.")]
    public float VerticalOffset = 0f;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private CanvasGroup _canvasGroup;
    private Camera _camera;
    private TargetData _currentTarget;
    private bool _isVisible = false;
    private Coroutine _fadeCoroutine;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        _camera = Camera.main;

        _canvasGroup = ViewerCanvas.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = ViewerCanvas.gameObject.AddComponent<CanvasGroup>();

        // Start hidden
        _canvasGroup.alpha = 0f;
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;
        ViewerCanvas.gameObject.SetActive(false);

        // Wire buttons
        if (CloseButton != null)
            CloseButton.onClick.AddListener(Hide);

        if (InfoTabButton != null)
            InfoTabButton.onClick.AddListener(() => ShowTab("info"));

        if (VideoTabButton != null)
            VideoTabButton.onClick.AddListener(() => ShowTab("video"));

        if (ModelTabButton != null)
            ModelTabButton.onClick.AddListener(() => ShowTab("model"));
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Show the content viewer for a specific target.
    /// Positions it in front of the user's current view.
    /// </summary>
    public void Show(TargetData data)
    {
        _currentTarget = data;

        // Position in front of camera
        PositionInFrontOfCamera();

        // Populate content
        PopulateContent(data);

        // Show with fade
        ViewerCanvas.gameObject.SetActive(true);
        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeTo(1f));

        _isVisible = true;
        Debug.Log($"[ContentViewer] Showing content for: {data.name}");
    }

    /// <summary>
    /// Hide the content viewer.
    /// </summary>
    public void Hide()
    {
        if (!_isVisible) return;

        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;

        // Stop video if playing
        if (VideoPlayer != null && VideoPlayer.isPlaying)
            VideoPlayer.Stop();

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeToAndDisable(0f));

        _isVisible = false;
    }

    // -------------------------------------------------------------------------
    // Content Population
    // -------------------------------------------------------------------------

    private void PopulateContent(TargetData data)
    {
        // Header
        if (TitleLabel != null)
            TitleLabel.text = data.name;

        // Info panel
        if (DescriptionLabel != null)
            DescriptionLabel.text = data.description;

        if (ContentTextLabel != null)
            ContentTextLabel.text = data.content?.text ?? "";

        // Show/hide tabs based on available content
        bool hasVideo = !string.IsNullOrEmpty(data.content?.video_url);
        bool hasModel = !string.IsNullOrEmpty(data.content?.model_3d_url);

        if (VideoTabButton != null)
            VideoTabButton.gameObject.SetActive(hasVideo);

        if (ModelTabButton != null)
            ModelTabButton.gameObject.SetActive(hasModel);

        // Load video URL if available
        if (hasVideo && VideoPlayer != null)
        {
            VideoPlayer.url = data.content.video_url;
            VideoPlayer.Prepare();
        }

        // Default to info tab
        ShowTab("info");
    }

    private void ShowTab(string tab)
    {
        if (InfoPanel != null)
            InfoPanel.SetActive(tab == "info");

        if (VideoPanel != null)
        {
            VideoPanel.SetActive(tab == "video");
            if (tab == "video" && VideoPlayer != null && VideoPlayer.isPrepared)
                VideoPlayer.Play();
            else if (tab != "video" && VideoPlayer != null && VideoPlayer.isPlaying)
                VideoPlayer.Pause();
        }

        if (ModelPanel != null)
            ModelPanel.SetActive(tab == "model");

        // Update tab button visuals
        SetTabActive(InfoTabButton, tab == "info");
        SetTabActive(VideoTabButton, tab == "video");
        SetTabActive(ModelTabButton, tab == "model");
    }

    private void SetTabActive(Button btn, bool active)
    {
        if (btn == null) return;
        var colors = btn.colors;
        colors.normalColor = active
            ? new Color(0.9f, 0.2f, 0.2f, 1f)   // active: red
            : new Color(0.2f, 0.2f, 0.2f, 1f);   // inactive: dark
        btn.colors = colors;
    }

    // -------------------------------------------------------------------------
    // Positioning
    // -------------------------------------------------------------------------

    private void PositionInFrontOfCamera()
    {
        if (_camera == null) return;

        Vector3 forward = _camera.transform.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 position = _camera.transform.position
                         + forward * ViewerDistance
                         + Vector3.up * VerticalOffset;

        ViewerCanvas.transform.position = position;
        ViewerCanvas.transform.rotation = Quaternion.LookRotation(
            forward, Vector3.up);
    }

    // -------------------------------------------------------------------------
    // Fade
    // -------------------------------------------------------------------------

    private IEnumerator FadeTo(float target)
    {
        float start = _canvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < FadeDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Lerp(start, target, elapsed / FadeDuration);
            yield return null;
        }
        _canvasGroup.alpha = target;
    }

    private IEnumerator FadeToAndDisable(float target)
    {
        yield return StartCoroutine(FadeTo(target));
        ViewerCanvas.gameObject.SetActive(false);
    }
}
