using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// SignMenuController
/// Manages the World Space Canvas that appears over a detected sign.
/// Handles show/hide/close logic and icon layout.
///
/// RULES:
///   - Showing is ONLY triggered by a fresh scan (QuestImageDetector calls Show())
///   - User can close manually via CloseButton
///   - If user closed and sign is re-detected, content respawns (user-closed flag clears)
///   - If sign is lost (not closed by user), canvas hides and reappears on re-detection
/// </summary>
public class SignMenuController : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Canvas Root")]
    [Tooltip("The root GameObject of this World Space Canvas. Assign the Canvas itself.")]
    public Canvas WorldCanvas;

    [Header("Icon Buttons")]
    [Tooltip("The Info icon button GameObject")]
    public GameObject InfoIcon;

    [Tooltip("The Warning icon button GameObject")]
    public GameObject WarningIcon;

    [Tooltip("The Checklist icon button GameObject")]
    public GameObject ChecklistIcon;

    [Header("Close Button")]
    [Tooltip("The close (X) button the user taps to dismiss the menu")]
    public Button CloseButton;

    [Header("Animation")]
    [Tooltip("How fast the canvas fades in/out")]
    public float FadeDuration = 0.3f;

    [Header("Icon Spacing")]
    [Tooltip("Horizontal spacing between icons")]
    public float IconSpacing = 0.08f;

    [Tooltip("Vertical offset of icons above center of sign")]
    public float IconVerticalOffset = 0.06f;

    // -------------------------------------------------------------------------
    // Private State
    // -------------------------------------------------------------------------

    private bool _userClosed = false;
    private bool _isVisible = false;
    private CanvasGroup _canvasGroup;
    private Coroutine _fadeCoroutine;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        // Get or add CanvasGroup for fading
        _canvasGroup = WorldCanvas.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = WorldCanvas.gameObject.AddComponent<CanvasGroup>();

        // Start hidden
        _canvasGroup.alpha = 0f;
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;
        WorldCanvas.gameObject.SetActive(false);

        // Wire close button
        if (CloseButton != null)
            CloseButton.onClick.AddListener(OnUserClose);
    }

    // -------------------------------------------------------------------------
    // Public API (called by QuestImageDetector)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called when the sign is detected and world position is known.
    /// position = world center of the sign
    /// rotation = orientation aligned to the sign plane
    /// </summary>
    public void OnSignDetected(Vector3 position, Quaternion rotation)
    {
        // Move canvas to sign position
        transform.position = position;
        transform.rotation = rotation;

        // If user had closed it before, a fresh detection respawns it
        // (this is the ONLY way it can reopen after being closed)
        if (_userClosed)
        {
            _userClosed = false;
            Debug.Log("[SignMenuController] Fresh detection after user close — respawning.");
        }

        if (!_isVisible)
            Show();
    }

    /// <summary>
    /// Called when the sign is lost from view.
    /// If user closed it, we do nothing. Otherwise we hide it.
    /// </summary>
    public void OnSignLost()
    {
        if (_userClosed)
            return; // already hidden by user, do nothing

        if (_isVisible)
            Hide();
    }

    // -------------------------------------------------------------------------
    // Show / Hide
    // -------------------------------------------------------------------------

    public void Show()
    {
        if (_userClosed) return;

        _isVisible = true;
        WorldCanvas.gameObject.SetActive(true);
        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        if (_fadeCoroutine != null)
            StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeTo(1f));

        PositionIcons();
    }

    public void Hide()
    {
        _isVisible = false;
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;

        if (_fadeCoroutine != null)
            StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeToAndDisable(0f));
    }

    // -------------------------------------------------------------------------
    // User Close
    // -------------------------------------------------------------------------

    public void OnUserClose()
    {
        Debug.Log("[SignMenuController] User closed the menu.");
        _userClosed = true;
        Hide();
    }

    // -------------------------------------------------------------------------
    // Icon Positioning
    // -------------------------------------------------------------------------

    /// <summary>
    /// Arranges icons horizontally around the center of the canvas in local space.
    /// </summary>
    private void PositionIcons()
    {
        GameObject[] icons = { InfoIcon, WarningIcon, ChecklistIcon };
        int count = icons.Length;
        float totalWidth = (count - 1) * IconSpacing;
        float startX = -totalWidth / 2f;

        for (int i = 0; i < count; i++)
        {
            if (icons[i] == null) continue;
            RectTransform rt = icons[i].GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchoredPosition3D = new Vector3(
                    startX + i * IconSpacing,
                    IconVerticalOffset,
                    0f
                );
            }
        }
    }

    // -------------------------------------------------------------------------
    // Fade Coroutines
    // -------------------------------------------------------------------------

    private IEnumerator FadeTo(float targetAlpha)
    {
        float startAlpha = _canvasGroup.alpha;
        float elapsed = 0f;

        while (elapsed < FadeDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / FadeDuration);
            yield return null;
        }

        _canvasGroup.alpha = targetAlpha;
    }

    private IEnumerator FadeToAndDisable(float targetAlpha)
    {
        yield return StartCoroutine(FadeTo(targetAlpha));
        WorldCanvas.gameObject.SetActive(false);
    }

    // -------------------------------------------------------------------------
    // Icon Button Handlers (wire these up or override in subclass)
    // -------------------------------------------------------------------------

    public void OnInfoIconClicked()
    {
        Debug.Log("[SignMenuController] Info icon clicked.");
        // TODO: Show info panel
    }

    public void OnWarningIconClicked()
    {
        Debug.Log("[SignMenuController] Warning icon clicked.");
        // TODO: Show warning panel
    }

    public void OnChecklistIconClicked()
    {
        Debug.Log("[SignMenuController] Checklist icon clicked.");
        // TODO: Show checklist panel
    }
}
