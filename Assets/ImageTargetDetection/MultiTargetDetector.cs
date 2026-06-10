using System.Collections;
using System.Collections.Generic;
using System.Threading;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Features2dModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using Meta.XR;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// MultiTargetDetector
///
/// Detects multiple target images simultaneously in the Quest 3
/// passthrough camera feed. For each detected target, computes
/// its world position and notifies the NotificationManager.
///
/// Works together with:
///   - TargetLibraryManager (provides loaded targets + descriptors)
///   - NotificationManager (shows/hides per-target notification bubbles)
/// </summary>
public class MultiTargetDetector : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Passthrough Camera")]
    [Tooltip("Assign the PassthroughCameraAccess component.")]
    public PassthroughCameraAccess CameraAccess;

    [Header("Capture Resolution")]
    public int CaptureWidth = 1280;
    public int CaptureHeight = 960;

    [Header("Detection Settings")]
    [Range(5, 50)] public int MinMatchCount = 10;
    [Range(1, 10)] public int ProcessEveryNFrames = 2;
    [Range(0.5f, 0.9f)] public float LoweRatioThreshold = 0.75f;

    [Header("World Space")]
    [Tooltip("CenterEyeAnchor camera.")]
    public Camera XRCamera;

    [Tooltip("Estimated distance to signs in meters.")]
    [Range(0.2f, 5f)]
    public float EstimatedSignDistance = 1.5f;

    [Header("References")]
    [Tooltip("Assign the NotificationManager component.")]
    public NotificationManager NotificationManager;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private List<LoadedTarget> _targets = new();
    private DescriptorMatcher _matcher;
    private bool _initialized = false;
    private int _frameCounter = 0;

    private RenderTexture _rt;
    private Texture2D _readTex;
    private Texture _cameraTexture;

    // Track which targets are currently detected
    private HashSet<string> _currentlyDetected = new();

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private IEnumerator Start()
    {
        if (XRCamera == null)
            XRCamera = Camera.main;

        // Wait for passthrough camera
        Debug.Log("[MultiTargetDetector] Waiting for passthrough camera...");
        while (CameraAccess == null || !CameraAccess.IsPlaying)
            yield return null;

        _cameraTexture = CameraAccess.GetTexture();
        Debug.Log($"[MultiTargetDetector] Camera ready: {_cameraTexture.width}x{_cameraTexture.height}");

        // Setup capture buffers
        _rt = new RenderTexture(CaptureWidth, CaptureHeight, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();
        _readTex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

        // Initialize FLANN matcher (shared across all targets)
        _matcher = DescriptorMatcher.create(DescriptorMatcher.FLANNBASED);
    }

    private void Update()
    {
        if (!_initialized) return;
        if (CameraAccess.IsPlaying)
            _cameraTexture = CameraAccess.GetTexture();

        _frameCounter++;
        if (_frameCounter % ProcessEveryNFrames != 0) return;

        DetectAllTargets();
    }

    private void OnDestroy()
    {
        _matcher?.Dispose();
        if (_rt != null) { _rt.Release(); _rt = null; }
        if (_readTex != null) { Destroy(_readTex); _readTex = null; }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by TargetLibraryManager when all targets are loaded and ready.
    /// </summary>
    public void InitializeWithTargets(List<LoadedTarget> targets)
    {
        _targets = targets;
        _initialized = true;
        Debug.Log($"[MultiTargetDetector] Initialized with {targets.Count} target(s).");
    }

    // -------------------------------------------------------------------------
    // Detection
    // -------------------------------------------------------------------------

    private void DetectAllTargets()
    {
        if (_cameraTexture == null || _targets.Count == 0) return;

        // Capture frame from passthrough camera
        Graphics.Blit(_cameraTexture, _rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readTex.ReadPixels(new UnityEngine.Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
        _readTex.Apply(false, false);
        RenderTexture.active = prev;

        // Convert to grayscale OpenCV Mat
        Mat frameMat = new Mat(CaptureHeight, CaptureWidth, CvType.CV_8UC3);
        OpenCVMatUtils.Texture2DToMat(_readTex, frameMat);
        Mat frameGray = new Mat();
        Imgproc.cvtColor(frameMat, frameGray, Imgproc.COLOR_RGB2GRAY);
        frameMat.Dispose();

        // Detect keypoints in current frame (once, shared across all targets)
        SIFT detector = SIFT.create();
        MatOfKeyPoint frameKeypoints = new MatOfKeyPoint();
        Mat frameDescriptors = new Mat();
        detector.detectAndCompute(frameGray, new Mat(), frameKeypoints, frameDescriptors);
        detector.Dispose();
        frameGray.Dispose();

        if (frameDescriptors.empty() || frameKeypoints.total() == 0)
        {
            // Nothing detected in frame at all
            HandleNoDetections();
            frameKeypoints.Dispose();
            frameDescriptors.Dispose();
            return;
        }

        // Try to match each target against the frame
        HashSet<string> detectedThisFrame = new();

        foreach (var target in _targets)
        {
            bool detected = TryMatchTarget(
                target,
                frameKeypoints,
                frameDescriptors,
                out Vector3 worldCenter,
                out Quaternion worldRotation,
                out Vector3[] worldCorners);

            if (detected)
            {
                detectedThisFrame.Add(target.Data.id);

                // Notify NotificationManager
                if (NotificationManager != null)
                    NotificationManager.OnTargetDetected(
                        target.Data, worldCenter, worldRotation, worldCorners);
            }
        }

        // Notify about lost targets
        foreach (var id in _currentlyDetected)
        {
            if (!detectedThisFrame.Contains(id))
            {
                if (NotificationManager != null)
                    NotificationManager.OnTargetLost(id);
            }
        }

        _currentlyDetected = detectedThisFrame;

        frameKeypoints.Dispose();
        frameDescriptors.Dispose();
    }

    private bool TryMatchTarget(
        LoadedTarget target,
        MatOfKeyPoint frameKeypoints,
        Mat frameDescriptors,
        out Vector3 worldCenter,
        out Quaternion worldRotation,
        out Vector3[] worldCorners)
    {
        worldCenter = Vector3.zero;
        worldRotation = Quaternion.identity;
        worldCorners = null;

        // KNN match
        List<MatOfDMatch> knnMatches = new();
        _matcher.knnMatch(target.Descriptors, frameDescriptors, knnMatches, 2);

        // Lowe's ratio test
        List<DMatch> goodMatches = new();
        foreach (var match in knnMatches)
        {
            if (match.rows() > 1)
            {
                DMatch[] m = match.toArray();
                if (m[0].distance < LoweRatioThreshold * m[1].distance)
                    goodMatches.Add(m[0]);
            }
        }

        foreach (var m in knnMatches) m.Dispose();

        if (goodMatches.Count < MinMatchCount)
            return false;

        // Find homography
        List<KeyPoint> refKpList = target.Keypoints.toList();
        List<KeyPoint> frameKpList = frameKeypoints.toList();
        List<Point> refPoints = new();
        List<Point> framePoints = new();

        foreach (var match in goodMatches)
        {
            refPoints.Add(refKpList[match.queryIdx].pt);
            framePoints.Add(frameKpList[match.trainIdx].pt);
        }

        MatOfPoint2f refMat2f = new MatOfPoint2f(refPoints.ToArray());
        MatOfPoint2f frameMat2f = new MatOfPoint2f(framePoints.ToArray());
        Mat H = Calib3d.findHomography(refMat2f, frameMat2f, Calib3d.RANSAC, 3.0);

        refMat2f.Dispose();
        frameMat2f.Dispose();

        if (H == null || H.empty())
        {
            H?.Dispose();
            return false;
        }

        // Get reference image size from texture
        int refWidth = target.Texture.width;
        int refHeight = target.Texture.height;

        List<Point> refCorners = new()
        {
            new Point(0, 0),
            new Point(refWidth, 0),
            new Point(refWidth, refHeight),
            new Point(0, refHeight)
        };

        MatOfPoint2f refCornersMat = new MatOfPoint2f(refCorners.ToArray());
        MatOfPoint2f sceneCornersMat = new MatOfPoint2f();
        Core.perspectiveTransform(refCornersMat, sceneCornersMat, H);
        List<Point> detectedCorners = new(sceneCornersMat.toList());

        H.Dispose();
        refCornersMat.Dispose();
        sceneCornersMat.Dispose();

        if (!IsHomographyValid(detectedCorners))
            return false;

        // Compute world pose
        ComputeWorldPose(detectedCorners, out worldCenter, out worldRotation, out worldCorners);
        return true;
    }

    private void HandleNoDetections()
    {
        foreach (var id in _currentlyDetected)
        {
            if (NotificationManager != null)
                NotificationManager.OnTargetLost(id);
        }
        _currentlyDetected.Clear();
    }

    // -------------------------------------------------------------------------
    // World Pose
    // -------------------------------------------------------------------------

    private void ComputeWorldPose(
        List<Point> corners,
        out Vector3 worldCenter,
        out Quaternion worldRotation,
        out Vector3[] worldCorners)
    {
        worldCorners = new Vector3[4];
        for (int i = 0; i < 4; i++)
            worldCorners[i] = CornerToWorld(corners[i]);

        worldCenter = (worldCorners[0] + worldCorners[1] +
                       worldCorners[2] + worldCorners[3]) / 4f;

        Vector3 right = (worldCorners[1] - worldCorners[0]).normalized;
        Vector3 up = -(worldCorners[3] - worldCorners[0]).normalized;

        if (right == Vector3.zero || up == Vector3.zero)
        {
            worldRotation = XRCamera.transform.rotation;
            return;
        }

        Vector3 forward = Vector3.Cross(right, up).normalized;
        worldRotation = Quaternion.LookRotation(forward, up);
    }

    private Vector3 CornerToWorld(Point corner)
    {
        float u = (float)corner.x / CaptureWidth;
        float v = 1f - ((float)corner.y / CaptureHeight);
        Ray ray = CameraAccess.ViewportPointToRay(new Vector2(u, v));
        return ray.origin + ray.direction * EstimatedSignDistance;
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    private bool IsHomographyValid(List<Point> corners)
    {
        if (corners == null || corners.Count != 4) return false;

        MatOfPoint2f cornersMat = new MatOfPoint2f(corners.ToArray());
        MatOfPoint cornersInt = new MatOfPoint();
        cornersMat.convertTo(cornersInt, CvType.CV_32S);
        bool isConvex = Imgproc.isContourConvex(cornersInt);
        cornersMat.Dispose();
        cornersInt.Dispose();

        if (!isConvex) return false;

        double area = Imgproc.contourArea(new MatOfPoint2f(corners.ToArray()));
        double frameArea = CaptureWidth * CaptureHeight;
        if (area < frameArea * 0.005 || area > frameArea * 0.8) return false;

        return true;
    }
}
