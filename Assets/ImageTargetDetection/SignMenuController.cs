using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// SignMenuController - State Machine Version
///
/// States:
///   IDLE        → never shown yet, waiting for first detection
///   SHOWING     → UI is visible, user can interact or close
///   USER_CLOSED → user dismissed, waiting for a fresh scan to reopen
///
/// Rules:
///   - Detection ALWAYS updates world position while SHOWING
///   - Detection triggers show only from IDLE or USER_CLOSED states
///   - Losing the image does NOT hide the UI (it stays until user closes)
///   - User closing sets USER_CLOSED state
///   - Fresh detection after USER_CLOSED → back to SHOWING (respawns content)
///   - ProcessEveryNFrames flickering does NOT affect UI visibility
/// </summary>
public class SignMenuController : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private enum MenuState { Idle, Showing, UserClosed }
    private MenuState _state = MenuState.Idle;

    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Canvas")]
    [Tooltip("The World Space Canvas root GameObject.")]
    public Canvas WorldCanvas;

    [Header("Icons")]
    public GameObject InfoIcon;
    public GameObject WarningIcon;
    public GameObject ChecklistIcon;

    [Header("Close Button")]
    public Button CloseButton;

    [Header("Animation")]
    public float FadeDuration = 0.3f;

    [Header("Icon Layout")]
    public float IconSpacing = 0.08f;
    public float IconVerticalOffset = 0.06f;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private CanvasGroup _canvasGroup;
    private Coroutine _fadeCoroutine;

    // Tracks whether detection is currently active
    // Used to detect a "fresh scan" after USER_CLOSED
    private bool _wasDetectedLastFrame = false;
    private bool _freshScanTriggered = false;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        _canvasGroup = WorldCanvas.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = WorldCanvas.gameObject.AddComponent<CanvasGroup>();

        // Start fully hidden
        _canvasGroup.alpha = 0f;
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;
        WorldCanvas.gameObject.SetActive(false);

        if (CloseButton != null)
            CloseButton.onClick.AddListener(OnUserClose);
    }

    // -------------------------------------------------------------------------
    // Public API — called by QuestImageDetector
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called every frame while the sign is detected.
    /// Updates position and handles state transitions.
    /// </summary>
    public void OnSignDetected(Vector3 position, Quaternion rotation)
    {
        // Always update canvas position while detected
        transform.position = position;
        transform.rotation = rotation;

        switch (_state)
        {
            case MenuState.Idle:
                // First ever detection — show the UI
                Debug.Log("[SignMenuController] First detection — showing UI.");
                _state = MenuState.Showing;
                Show();
                break;

            case MenuState.Showing:
                // Already showing — just keep updating position
                // No state change needed
                break;

            case MenuState.UserClosed:
                // User had closed it.
                // Only reopen if this is a FRESH scan
                // (image was lost and found again, not just continuously tracked)
                if (_freshScanTriggered)
                {
                    Debug.Log("[SignMenuController] Fresh scan after close — reopening UI.");
                    _freshScanTriggered = false;
                    _state = MenuState.Showing;
                    Show();
                }
                break;
        }

        _wasDetectedLastFrame = true;
    }

    /// <summary>
    /// Called when the sign is lost from view.
    /// Does NOT hide the UI — just tracks the gap for fresh scan detection.
    /// </summary>
    public void OnSignLost()
    {
        if (_wasDetectedLastFrame)
        {
            // Image was just lost — mark that a gap occurred
            // Next detection after this gap = fresh scan
            if (_state == MenuState.UserClosed)
                _freshScanTriggered = true;

            _wasDetectedLastFrame = false;
            Debug.Log("[SignMenuController] Sign lost. UI stays visible.");
        }
    }

    // -------------------------------------------------------------------------
    // User Close
    // -------------------------------------------------------------------------

    public void OnUserClose()
    {
        Debug.Log("[SignMenuController] User closed the menu.");
        _state = MenuState.UserClosed;
        _freshScanTriggered = false; // reset — need a real gap before reopening
        Hide();
    }

    // -------------------------------------------------------------------------
    // Show / Hide
    // -------------------------------------------------------------------------

    private void Show()
    {
        WorldCanvas.gameObject.SetActive(true);
        _canvasGroup.interactable = true;
        _canvasGroup.blocksRaycasts = true;

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeTo(1f));

        PositionIcons();
    }

    private void Hide()
    {
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeToAndDisable(0f));
    }

    // -------------------------------------------------------------------------
    // Icon Positioning
    // -------------------------------------------------------------------------

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
                rt.anchoredPosition3D = new Vector3(
                    startX + i * IconSpacing,
                    IconVerticalOffset,
                    0f);
        }
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
        WorldCanvas.gameObject.SetActive(false);
    }

    // -------------------------------------------------------------------------
    // Icon Click Handlers
    // -------------------------------------------------------------------------

    public void OnInfoIconClicked()
    {
        Debug.Log("[SignMenuController] Info clicked.");
        // TODO: show info panel
    }

    public void OnWarningIconClicked()
    {
        Debug.Log("[SignMenuController] Warning clicked.");
        // TODO: show warning panel
    }

    public void OnChecklistIconClicked()
    {
        Debug.Log("[SignMenuController] Checklist clicked.");
        // TODO: show checklist panel
    }
}
