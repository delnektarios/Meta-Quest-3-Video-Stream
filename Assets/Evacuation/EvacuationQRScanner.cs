using System.Collections;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// EvacuationQRScanner (v2 — API fetch)
///
/// Scans for a tiny QR code containing just a plan ID:
///   {"id":"a3b2c1d4e5f6"}
///
/// Then fetches the full EvacuationPlan JSON from the API:
///   GET /api/floorplans/{id}
///
/// Passes the parsed plan + world origin/forward to EvacuationManager.
///
/// SETUP:
///   1. Attach to a GameObject in your scene
///   2. Set ApiBaseUrl and ApiToken to match your HuggingFace Space
///   3. Assign CameraAccess, XRCamera, EvacuationManager
///   4. User stands at START location, faces FORWARD direction, scans QR
/// </summary>
public class EvacuationQRScanner : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("API Settings")]
    [Tooltip("Base URL of the SAFER AR HuggingFace Space.")]
    public string ApiBaseUrl = "https://noesishub-company-safer-ar-app.hf.space";

    [Tooltip("API token for authentication.")]
    public string ApiToken = "safer-quest-2026-xyz";

    [Header("Passthrough Camera")]
    public PassthroughCameraAccess CameraAccess;

    [Header("World Space")]
    public Camera XRCamera;

    [Header("References")]
    public EvacuationManager EvacuationManager;

    [Header("Scanning Settings")]
    [Tooltip("How often to attempt QR detection (seconds).")]
    [Range(0.1f, 2f)]
    public float ScanInterval = 0.3f;

    [Tooltip("Show debug log when scanning.")]
    public bool DebugLogging = true;

    [Header("Capture Resolution")]
    public int CaptureWidth = 1280;
    public int CaptureHeight = 960;

    [Header("Events")]
    public UnityEvent OnQRDetected;
    public UnityEvent OnPlanFetched;
    public UnityEvent OnQRInvalid;
    public UnityEvent OnFetchFailed;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private RenderTexture _rt;
    private Texture2D _readTex;
    private Texture _cameraTexture;
    private QRCodeDetector _qrDetector;

    private bool _initialized = false;
    private bool _scanning = true;
    private bool _hasScanned = false;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private IEnumerator Start()
    {
        if (XRCamera == null) XRCamera = Camera.main;

        Debug.Log("[QRScanner] Waiting for passthrough camera...");
        while (CameraAccess == null || !CameraAccess.IsPlaying)
            yield return null;

        _cameraTexture = CameraAccess.GetTexture();
        Debug.Log($"[QRScanner] Camera ready: {_cameraTexture.width}x{_cameraTexture.height}");

        _rt = new RenderTexture(CaptureWidth, CaptureHeight, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();
        _readTex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

        _qrDetector = new QRCodeDetector();
        _initialized = true;

        Debug.Log("[QRScanner] Ready. Point camera at evacuation QR code...");
        StartCoroutine(ScanLoop());
    }

    private void OnDestroy()
    {
        _qrDetector?.Dispose();
        if (_rt != null) { _rt.Release(); _rt = null; }
        if (_readTex != null) { Destroy(_readTex); _readTex = null; }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void StartScanning()
    {
        _scanning = true;
        _hasScanned = false;
        Debug.Log("[QRScanner] Scanning started.");
    }

    public void StopScanning()
    {
        _scanning = false;
        Debug.Log("[QRScanner] Scanning stopped.");
    }

    // -------------------------------------------------------------------------
    // Scan Loop
    // -------------------------------------------------------------------------

    private IEnumerator ScanLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(ScanInterval);

            if (!_scanning || _hasScanned || !_initialized) continue;
            if (!CameraAccess.IsPlaying) continue;

            _cameraTexture = CameraAccess.GetTexture();
            if (_cameraTexture == null) continue;

            string result = ScanFrame();

            if (!string.IsNullOrEmpty(result))
            {
                if (DebugLogging)
                    Debug.Log($"[QRScanner] QR text detected: {result}");

                yield return StartCoroutine(ProcessQRResult(result));
            }
        }
    }

    private string ScanFrame()
    {
        Graphics.Blit(_cameraTexture, _rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readTex.ReadPixels(
            new UnityEngine.Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
        _readTex.Apply(false, false);
        RenderTexture.active = prev;

        Mat frameMat = new Mat(CaptureHeight, CaptureWidth, CvType.CV_8UC3);
        OpenCVMatUtils.Texture2DToMat(_readTex, frameMat);

        Mat grayMat = new Mat();
        Imgproc.cvtColor(frameMat, grayMat, Imgproc.COLOR_RGB2GRAY);
        frameMat.Dispose();

        Mat points = new Mat();
        string decoded = _qrDetector.detectAndDecode(grayMat, points);

        grayMat.Dispose();
        points.Dispose();

        return decoded;
    }

    // -------------------------------------------------------------------------
    // QR Processing
    // -------------------------------------------------------------------------

    private IEnumerator ProcessQRResult(string qrText)
    {
        // Parse the tiny QR payload: {"id":"a3b2c1d4e5f6"}
        string planId = ExtractPlanId(qrText);

        if (string.IsNullOrEmpty(planId))
        {
            if (DebugLogging)
                Debug.LogWarning($"[QRScanner] QR detected but not a SAFER AR QR: {qrText}");
            OnQRInvalid?.Invoke();
            yield break;
        }

        Debug.Log($"[QRScanner] ✅ Valid SAFER AR QR! Plan ID: {planId}");
        _hasScanned = true;
        _scanning = false;
        OnQRDetected?.Invoke();

        // Record world origin and forward at scan time
        Vector3 scanPosition = XRCamera.transform.position;
        Vector3 scanForward = XRCamera.transform.forward;
        scanForward.y = 0;
        scanForward.Normalize();

        // Fetch full plan from API
        yield return StartCoroutine(FetchPlan(planId, scanPosition, scanForward));
    }

    private string ExtractPlanId(string qrText)
    {
        // Expected format: {"id":"a3b2c1d4e5f6"}
        if (!qrText.Contains("\"id\"")) return null;

        try
        {
            // Simple JSON parse for just the id field
            var wrapper = JsonUtility.FromJson<PlanIdWrapper>(qrText);
            return wrapper?.id;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // API Fetch
    // -------------------------------------------------------------------------

    private IEnumerator FetchPlan(string planId, Vector3 scanPosition, Vector3 scanForward)
    {
        string url = $"{ApiBaseUrl}/api/floorplans/{planId}";
        Debug.Log($"[QRScanner] Fetching plan from: {url}");

        using var request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", $"Bearer {ApiToken}");
        request.timeout = 15;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[QRScanner] Failed to fetch plan: {request.error}");
            OnFetchFailed?.Invoke();
            // Allow re-scan
            _hasScanned = false;
            _scanning = true;
            yield break;
        }

        string json = request.downloadHandler.text;
        Debug.Log($"[QRScanner] Plan fetched: {json.Substring(0, Mathf.Min(100, json.Length))}...");

        EvacuationPlan plan;
        try
        {
            plan = EvacuationPlan.FromJson(json);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[QRScanner] Failed to parse plan JSON: {e.Message}");
            OnFetchFailed?.Invoke();
            _hasScanned = false;
            _scanning = true;
            yield break;
        }

        if (plan == null || plan.waypoints == null || plan.waypoints.Count == 0)
        {
            Debug.LogError("[QRScanner] Plan has no waypoints.");
            OnFetchFailed?.Invoke();
            _hasScanned = false;
            _scanning = true;
            yield break;
        }

        Debug.Log($"[QRScanner] ✅ Plan ready: {plan.waypoints.Count} waypoints, " +
                  $"destination at {plan.destination.distanceM:F1}m");

        // Pass to EvacuationManager
        if (EvacuationManager != null)
            EvacuationManager.InitializeFromQR(plan, scanPosition, scanForward);

        OnPlanFetched?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    [System.Serializable]
    private class PlanIdWrapper
    {
        public string id;
    }
}
