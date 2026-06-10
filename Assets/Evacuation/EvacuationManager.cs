using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// EvacuationManager
///
/// Core manager for the evacuation navigation system.
///
/// Flow:
///   1. EvacuationQRScanner calls InitializeFromQR() with parsed plan
///   2. Manager stores plan + world origin/forward
///   3. When alarm triggers → PlaceEvacuationArrows()
///   4. Arrows placed at correct world positions using
///      distance + bearing from QR origin
///   5. Arrows point toward next waypoint
///   6. User follows arrows to destination
///
/// SETUP:
///   1. Attach to a GameObject named "EvacuationManager"
///   2. Assign ArrowPrefab (see WaypointArrow.cs)
///   3. Assign XRCamera
///   4. Wire AlarmTriggered event or call TriggerAlarm() from code
/// </summary>
public class EvacuationManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Prefabs")]
    [Tooltip("Arrow prefab placed on the floor at each waypoint.")]
    public GameObject ArrowPrefab;

    [Tooltip("Destination marker prefab (shown at the exit).")]
    public GameObject DestinationPrefab;

    [Header("World Space")]
    public Camera XRCamera;

    [Header("Navigation Settings")]
    [Tooltip("How many arrows to show at once. 0 = show all.")]
    [Range(0, 10)]
    public int MaxVisibleArrows = 3;

    [Tooltip("Distance in meters to consider a waypoint 'reached'.")]
    [Range(0.5f, 5f)]
    public float WaypointReachDistance = 2f;

    [Tooltip("Height above floor to place arrows (meters).")]
    [Range(0f, 0.5f)]
    public float ArrowHeight = 0.05f;

    [Tooltip("Check for waypoint progress every N seconds.")]
    [Range(0.5f, 5f)]
    public float ProgressCheckInterval = 1f;

    [Header("State")]
    [Tooltip("Is the evacuation alarm currently active?")]
    public bool AlarmActive = false;

    [Header("Events")]
    public UnityEvent OnAlarmTriggered;
    public UnityEvent OnEvacuationComplete;
    public UnityEvent OnQRNotScanned; // fired if alarm triggered before QR scanned

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private EvacuationPlan _plan;
    private Vector3 _worldOrigin;
    private Vector3 _worldForward;

    private bool _planLoaded = false;
    private int _currentWaypointIndex = 0;

    private List<GameObject> _spawnedArrows = new();
    private GameObject _destinationMarker;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (XRCamera == null) XRCamera = Camera.main;
    }

    private void Start()
    {
        // Hide everything initially
        ClearArrows();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by EvacuationQRScanner when a valid QR is scanned.
    /// </summary>
    public void InitializeFromQR(EvacuationPlan plan, Vector3 worldOrigin, Vector3 worldForward)
    {
        _plan = plan;
        _worldOrigin = worldOrigin;
        _worldForward = worldForward;
        _planLoaded = true;
        _currentWaypointIndex = 0;

        Debug.Log($"[EvacuationManager] Plan loaded: {plan.waypoints.Count} waypoints.");
        Debug.Log($"[EvacuationManager] Origin: {worldOrigin}, Forward: {worldForward}");
    }

    /// <summary>
    /// Trigger the evacuation alarm and start navigation.
    /// Call this from a button, alarm system, or remote trigger.
    /// </summary>
    public void TriggerAlarm()
    {
        if (!_planLoaded)
        {
            Debug.LogWarning("[EvacuationManager] Alarm triggered but no QR scanned yet!");
            OnQRNotScanned?.Invoke();
            return;
        }

        if (AlarmActive) return;

        AlarmActive = true;
        _currentWaypointIndex = 0;

        Debug.Log("[EvacuationManager] 🚨 ALARM TRIGGERED — placing evacuation arrows!");
        OnAlarmTriggered?.Invoke();

        PlaceEvacuationArrows();
        StartCoroutine(ProgressCheckLoop());
    }

    /// <summary>
    /// Stop the evacuation (e.g. drill complete or false alarm).
    /// </summary>
    public void StopEvacuation()
    {
        AlarmActive = false;
        ClearArrows();
        StopAllCoroutines();
        Debug.Log("[EvacuationManager] Evacuation stopped.");
    }

    // -------------------------------------------------------------------------
    // Arrow Placement
    // -------------------------------------------------------------------------

    private void PlaceEvacuationArrows()
    {
        ClearArrows();

        int total = _plan.waypoints.Count;
        int start = _currentWaypointIndex;
        int end = MaxVisibleArrows > 0
            ? Mathf.Min(start + MaxVisibleArrows, total)
            : total;

        // Place waypoint arrows
        for (int i = start; i < end; i++)
        {
            var wp = _plan.waypoints[i];
            Vector3 worldPos = CalculateWorldPosition(wp.distanceM, wp.bearingRel);
            worldPos.y = XRCamera.transform.position.y - 1f + ArrowHeight; // floor level

            // Direction to next waypoint (or destination if last)
            Vector3 nextPos;
            if (i + 1 < total)
            {
                var next = _plan.waypoints[i + 1];
                nextPos = CalculateWorldPosition(next.distanceM, next.bearingRel);
            }
            else
            {
                nextPos = CalculateWorldPosition(
                    _plan.destination.distanceM, _plan.destination.bearingRel);
            }
            nextPos.y = worldPos.y;

            GameObject arrow = Instantiate(ArrowPrefab, worldPos, Quaternion.identity);

            // Point arrow toward next position
            Vector3 dir = (nextPos - worldPos).normalized;
            if (dir != Vector3.zero)
                arrow.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            // Label
            WaypointArrow wa = arrow.GetComponent<WaypointArrow>();
            if (wa != null)
                wa.SetupArrow(i + 1, wp.distanceM, i == start);

            _spawnedArrows.Add(arrow);
            Debug.Log($"[EvacuationManager] Arrow {i+1} placed at {worldPos} " +
                      $"(dist:{wp.distanceM:F1}m bearing:{wp.bearingRel:F1}°)");
        }

        // Place destination marker
        if (_destinationMarker != null) Destroy(_destinationMarker);
        if (DestinationPrefab != null)
        {
            Vector3 destPos = CalculateWorldPosition(
                _plan.destination.distanceM, _plan.destination.bearingRel);
            destPos.y = XRCamera.transform.position.y - 1f + 0.1f;
            _destinationMarker = Instantiate(DestinationPrefab, destPos, Quaternion.identity);
            Debug.Log($"[EvacuationManager] Destination marker placed at {destPos}");
        }
    }

    // -------------------------------------------------------------------------
    // Progress Tracking
    // -------------------------------------------------------------------------

    private IEnumerator ProgressCheckLoop()
    {
        while (AlarmActive)
        {
            yield return new WaitForSeconds(ProgressCheckInterval);
            CheckWaypointProgress();
        }
    }

    private void CheckWaypointProgress()
    {
        if (_currentWaypointIndex >= _plan.waypoints.Count)
        {
            // Check if reached destination
            Vector3 destPos = CalculateWorldPosition(
                _plan.destination.distanceM, _plan.destination.bearingRel);
            float distToDest = Vector3.Distance(
                new Vector3(XRCamera.transform.position.x, 0, XRCamera.transform.position.z),
                new Vector3(destPos.x, 0, destPos.z));

            if (distToDest < WaypointReachDistance)
            {
                Debug.Log("[EvacuationManager] ✅ Destination reached! Evacuation complete.");
                OnEvacuationComplete?.Invoke();
                StopEvacuation();
            }
            return;
        }

        var wp = _plan.waypoints[_currentWaypointIndex];
        Vector3 wpPos = CalculateWorldPosition(wp.distanceM, wp.bearingRel);

        float dist = Vector3.Distance(
            new Vector3(XRCamera.transform.position.x, 0, XRCamera.transform.position.z),
            new Vector3(wpPos.x, 0, wpPos.z));

        if (dist < WaypointReachDistance)
        {
            Debug.Log($"[EvacuationManager] Waypoint {_currentWaypointIndex + 1} reached!");
            _currentWaypointIndex++;
            PlaceEvacuationArrows(); // refresh visible arrows
        }
    }

    // -------------------------------------------------------------------------
    // World Position Calculation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts distance + relative bearing into a Unity world position.
    ///
    /// bearingRel = degrees relative to forward direction
    ///   0° = straight ahead (forward)
    ///   90° = right
    ///   -90° = left
    ///   180°/-180° = behind
    /// </summary>
    private Vector3 CalculateWorldPosition(float distanceM, float bearingRel)
    {
        // Rotate world forward by bearing angle
        Quaternion rotation = Quaternion.AngleAxis(bearingRel, Vector3.up);
        Vector3 direction = rotation * _worldForward;
        direction.y = 0;
        direction.Normalize();

        return _worldOrigin + direction * distanceM;
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------

    private void ClearArrows()
    {
        foreach (var arrow in _spawnedArrows)
            if (arrow != null) Destroy(arrow);
        _spawnedArrows.Clear();

        if (_destinationMarker != null)
        {
            Destroy(_destinationMarker);
            _destinationMarker = null;
        }
    }

    private void OnDestroy()
    {
        ClearArrows();
    }
}
