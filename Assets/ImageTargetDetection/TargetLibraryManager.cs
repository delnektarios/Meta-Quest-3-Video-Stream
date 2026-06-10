using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Features2dModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

/// <summary>
/// TargetLibraryManager
///
/// On app start:
///   1. Fetches all targets from the SAFER AR API
///   2. Downloads each target image (with HF token for private dataset)
///   3. Builds SIFT descriptors for each image
///   4. Notifies MultiTargetDetector when ready
///
/// SETUP:
///   1. Attach to a GameObject named "TargetLibrary"
///   2. Set ApiBaseUrl, ApiToken and HFToken in the Inspector
///   3. Assign Detector (MultiTargetDetector)
/// </summary>
public class TargetLibraryManager : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("API Settings")]
    [Tooltip("Base URL of the SAFER AR HuggingFace Space.")]
    public string ApiBaseUrl = "https://noesishub-company-safer-ar-app.hf.space";

    [Tooltip("API token for the SAFER AR app endpoints.")]
    public string ApiToken = "safer-quest-2026-xyz";

    [Tooltip("HuggingFace token for downloading images from the private dataset. " +
             "Get from huggingface.co/settings/tokens (Read access).")]
    public string HFToken = "";

    [Header("References")]
    [Tooltip("Assign the MultiTargetDetector component.")]
    public MultiTargetDetector Detector;

    [Header("Events")]
    [Tooltip("Fired when all targets are loaded and ready.")]
    public UnityEvent OnLibraryReady;

    [Tooltip("Fired if loading fails.")]
    public UnityEvent OnLibraryFailed;

    // -------------------------------------------------------------------------
    // Public Properties
    // -------------------------------------------------------------------------

    /// <summary>All loaded targets with their SIFT data.</summary>
    public List<LoadedTarget> LoadedTargets { get; private set; } = new();

    /// <summary>True when all targets are downloaded and descriptors built.</summary>
    public bool IsReady { get; private set; } = false;

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private IEnumerator Start()
    {
        Debug.Log("[TargetLibrary] Starting — fetching targets from API...");
        yield return StartCoroutine(FetchAndLoadTargets());
    }

    private void OnDestroy()
    {
        foreach (var t in LoadedTargets)
            t.Dispose();
    }

    // -------------------------------------------------------------------------
    // Fetch + Load Pipeline
    // -------------------------------------------------------------------------

    private IEnumerator FetchAndLoadTargets()
    {
        // --- Step 1: Fetch target list from API ---
        string url = $"{ApiBaseUrl}/api/targets";
        Debug.Log($"[TargetLibrary] Fetching: {url}");

        using var request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", $"Bearer {ApiToken}");
        request.timeout = 30;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[TargetLibrary] Failed to fetch targets: {request.error}");
            OnLibraryFailed?.Invoke();
            yield break;
        }

        // --- Step 2: Parse JSON ---
        string json = request.downloadHandler.text;
        Debug.Log($"[TargetLibrary] Raw JSON: {json}");

        // Unity's JsonUtility doesn't support top-level arrays — wrap it
        string wrappedJson = $"{{\"targets\":{json}}}";
        TargetList targetList = JsonUtility.FromJson<TargetList>(wrappedJson);

        if (targetList == null || targetList.targets == null || targetList.targets.Count == 0)
        {
            Debug.LogWarning("[TargetLibrary] No targets found in API response.");
            OnLibraryFailed?.Invoke();
            yield break;
        }

        Debug.Log($"[TargetLibrary] Found {targetList.targets.Count} target(s). Downloading images...");

        // --- Step 3: Download each image and build SIFT descriptors ---
        int loaded = 0;
        int failed = 0;

        foreach (var target in targetList.targets)
        {
            yield return StartCoroutine(LoadTarget(target, (loadedTarget) =>
            {
                if (loadedTarget != null)
                {
                    LoadedTargets.Add(loadedTarget);
                    loaded++;
                    Debug.Log($"[TargetLibrary] Loaded: {target.name} " +
                              $"({loadedTarget.Keypoints.total()} keypoints)");
                }
                else
                {
                    failed++;
                    Debug.LogWarning($"[TargetLibrary] Failed to load: {target.name}");
                }
            }));
        }

        Debug.Log($"[TargetLibrary] Done. {loaded} loaded, {failed} failed.");

        if (loaded == 0)
        {
            Debug.LogError("[TargetLibrary] No targets loaded successfully.");
            OnLibraryFailed?.Invoke();
            yield break;
        }

        // --- Step 4: Notify detector ---
        IsReady = true;

        if (Detector != null)
            Detector.InitializeWithTargets(LoadedTargets);

        OnLibraryReady?.Invoke();
        Debug.Log("[TargetLibrary] Library ready!");
    }

    private IEnumerator LoadTarget(TargetData data, Action<LoadedTarget> callback)
    {
        using var imgRequest = UnityWebRequestTexture.GetTexture(data.target_image_url);

        // Images are stored in the private HuggingFace Dataset
        // so we need the HF token to download them
        if (!string.IsNullOrEmpty(HFToken))
            imgRequest.SetRequestHeader("Authorization", $"Bearer {HFToken}");

        imgRequest.timeout = 30;

        yield return imgRequest.SendWebRequest();

        if (imgRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[TargetLibrary] Image download failed for {data.name}: " +
                             $"{imgRequest.error} — URL: {data.target_image_url}");
            callback(null);
            yield break;
        }

        Texture2D texture = DownloadHandlerTexture.GetContent(imgRequest);

        if (texture == null)
        {
            Debug.LogWarning($"[TargetLibrary] Null texture for {data.name}");
            callback(null);
            yield break;
        }

        // Convert to OpenCV Mat and build SIFT descriptors
        LoadedTarget loadedTarget = BuildDescriptors(data, texture);
        callback(loadedTarget);
    }

    private LoadedTarget BuildDescriptors(TargetData data, Texture2D texture)
    {
        // Convert texture to grayscale OpenCV Mat
        Mat colorMat = new Mat(texture.height, texture.width, CvType.CV_8UC4);
        OpenCVMatUtils.Texture2DToMat(texture, colorMat);

        Mat grayMat = new Mat();
        Imgproc.cvtColor(colorMat, grayMat, Imgproc.COLOR_RGBA2GRAY);
        colorMat.Dispose();

        // Build SIFT descriptors
        SIFT detector = SIFT.create();
        MatOfKeyPoint keypoints = new MatOfKeyPoint();
        Mat descriptors = new Mat();
        detector.detectAndCompute(grayMat, new Mat(), keypoints, descriptors);
        detector.Dispose();
        grayMat.Dispose();

        if (descriptors.empty() || keypoints.total() == 0)
        {
            Debug.LogWarning($"[TargetLibrary] No keypoints found for {data.name}. " +
                             "Image may be too simple or low contrast.");
            keypoints.Dispose();
            descriptors.Dispose();
            return null;
        }

        return new LoadedTarget(data, keypoints, descriptors, texture);
    }
}

// -------------------------------------------------------------------------
// LoadedTarget — holds all data for one detected target
// -------------------------------------------------------------------------

/// <summary>
/// Holds the target metadata, SIFT descriptors, and texture for one target.
/// </summary>
public class LoadedTarget : IDisposable
{
    public TargetData Data { get; }
    public MatOfKeyPoint Keypoints { get; }
    public Mat Descriptors { get; }
    public Texture2D Texture { get; }

    private bool _disposed = false;

    public LoadedTarget(TargetData data, MatOfKeyPoint keypoints,
                        Mat descriptors, Texture2D texture)
    {
        Data = data;
        Keypoints = keypoints;
        Descriptors = descriptors;
        Texture = texture;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Keypoints?.Dispose();
        Descriptors?.Dispose();
        _disposed = true;
    }
}
