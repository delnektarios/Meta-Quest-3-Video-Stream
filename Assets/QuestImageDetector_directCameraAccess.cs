using System.Collections;
using System.Collections.Generic;
using System.Threading;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Features2dModule;
using OpenCVForUnity.ImgcodecsModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using PassthroughCameraSamples;
using Meta.XR;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// QuestImageDetector (v4 — Direct PassthroughCameraAccess)
///
/// Grabs the passthrough camera texture directly from PassthroughCameraAccess.GetTexture()
/// instead of reading from a RawImage. This is the correct approach for Quest 3.
///
/// SETUP:
///   1. Drag this script onto a GameObject in your scene.
///   2. Assign the PassthroughCameraAccess component (already in your scene).
///   3. Assign the SignMenuController.
///   4. Assign the LineRenderer for the world space green outline.
///   5. Assign XRCamera (CenterEyeAnchor) or leave empty for Camera.main.
///   6. Place your reference image in StreamingAssets and set ReferenceImagePath.
/// </summary>
public class QuestImageDetector_directCameraAccess : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Passthrough Camera")]
    [Tooltip("Assign the PassthroughCameraAccess component from your scene.")]
    public PassthroughCameraAccess CameraAccess;

    [Tooltip("Which eye camera to use for detection.")]
    public PassthroughCameraAccess.CameraPositionType CameraPosition =
        PassthroughCameraAccess.CameraPositionType.Left;

    [Header("Reference Image")]
    [Tooltip("Path relative to StreamingAssets. E.g: OpenCVForUnityExamples/features2d/firehose.png")]
    public string ReferenceImagePath = "OpenCVForUnityExamples/features2d/firehose.png";

    [Header("Capture Resolution")]
    public int CaptureWidth = 1280;
    public int CaptureHeight = 720;

    [Header("Detection Settings")]
    [Range(5, 50)] public int MinMatchCount = 12;
    [Range(1, 10)] public int ProcessEveryNFrames = 3;
    [Range(0.5f, 0.9f)] public float LoweRatioThreshold = 0.75f;

    [Header("Position Correction")]
    [Tooltip("Fine-tune the world position offset. Start with X=-0.06 to correct left camera offset.")]
    public Vector3 PositionOffset = new Vector3(-0.06f, 0f, 0f);

    [Header("World Space")]
    [Tooltip("CenterEyeAnchor camera. Leave empty to use Camera.main.")]
    public Camera XRCamera;

    [Tooltip("Estimated distance in meters from camera to sign.")]
    [Range(0.2f, 5f)]
    public float EstimatedSignDistance = 1.0f;

    [Header("World Space Outline")]
    [Tooltip("LineRenderer that draws the green rectangle over the physical sign.")]
    public LineRenderer OutlineRenderer;

    [Tooltip("Color of the outline.")]
    public Color OutlineColor = Color.green;

    [Tooltip("Width of the outline in meters.")]
    [Range(0.001f, 0.02f)]
    public float OutlineWidth = 0.005f;

    [Tooltip("Offset toward camera to avoid z-fighting with the sign surface.")]
    [Range(0f, 0.05f)]
    public float OutlineZOffset = 0.01f;

    [Header("Sign Menu")]
    [Tooltip("Assign the SignMenuController component.")]
    public SignMenuController SignMenu;

    [Header("Events")]
    public UnityEvent OnImageDetected;
    public UnityEvent OnImageLost;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    // OpenCV
    private Mat _referenceMat;
    private MatOfKeyPoint _referenceKeypoints;
    private Mat _referenceDescriptors;
    private SIFT _detector;
    private DescriptorMatcher _matcher;

    // Capture
    private RenderTexture _rt;
    private Texture2D _readTex;

    // Camera texture (from PassthroughCameraAccess)
    private Texture _cameraTexture;

    // State
    private bool _initialized = false;
    private bool _isDetected = false;
    private int _frameCounter = 0;
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------


    private IEnumerator Start()
    {
        if (CameraAccess == null)
        {
            Debug.LogError("[QuestImageDetector] CameraAccess is not assigned.");
            yield break;
        }

        if (XRCamera == null)
            XRCamera = Camera.main;

        // Setup LineRenderer
        SetupLineRenderer();

        // Wait for PassthroughCameraAccess to be ready
        Debug.Log("[QuestImageDetector] Waiting for PassthroughCameraAccess...");
        while (!CameraAccess.IsPlaying)
            yield return null;

        // Get the texture directly from PassthroughCameraAccess
        _cameraTexture = CameraAccess.GetTexture();

        if (_cameraTexture == null)
        {
            Debug.LogError("[QuestImageDetector] GetTexture() returned null. " +
                           "Make sure PassthroughCameraAccess is initialized and permission is granted.");
            yield break;
        }

        Debug.Log($"[QuestImageDetector] Camera texture ready: " +
                  $"{_cameraTexture.width}x{_cameraTexture.height}");

        // Create capture buffers
        _rt = new RenderTexture(CaptureWidth, CaptureHeight, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();
        _readTex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

        // Initialize OpenCV
        yield return StartCoroutine(InitializeOpenCV());
    }

    private IEnumerator InitializeOpenCV()
    {
        var task = OpenCVEnv.GetFilePathTaskAsync(ReferenceImagePath, cancellationToken: _cts.Token);
        while (!task.IsCompleted)
            yield return null;

        string filepath = task.Result;

        if (string.IsNullOrEmpty(filepath))
        {
            Debug.LogError($"[QuestImageDetector] Reference image not found: " +
                           $"StreamingAssets/{ReferenceImagePath}");
            yield break;
        }

        _referenceMat = Imgcodecs.imread(filepath, Imgcodecs.IMREAD_GRAYSCALE);

        if (_referenceMat.empty())
        {
            Debug.LogError($"[QuestImageDetector] Could not load image: {filepath}");
            yield break;
        }

        _detector = SIFT.create();
        _matcher = DescriptorMatcher.create(DescriptorMatcher.FLANNBASED);
        _referenceKeypoints = new MatOfKeyPoint();
        _referenceDescriptors = new Mat();
        _detector.detectAndCompute(
            _referenceMat, new Mat(), _referenceKeypoints, _referenceDescriptors);

        Debug.Log($"[QuestImageDetector] Ready. " +
                  $"Reference keypoints: {_referenceKeypoints.total()}");

        _initialized = true;
    }

    private void Update()
    {
        if (!_initialized) return;

        // Refresh texture reference each frame in case it changes
        if (CameraAccess.IsPlaying)
            _cameraTexture = CameraAccess.GetTexture();

        _frameCounter++;
        if (_frameCounter % ProcessEveryNFrames != 0) return;

        DetectInPassthroughFrame();
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _referenceMat?.Dispose();
        _referenceDescriptors?.Dispose();
        _referenceKeypoints?.Dispose();
        _detector?.Dispose();
        _matcher?.Dispose();
        if (_rt != null) { _rt.Release(); _rt = null; }
        if (_readTex != null) { Destroy(_readTex); _readTex = null; }
    }

    // -------------------------------------------------------------------------
    // Detection
    // -------------------------------------------------------------------------

    private void DetectInPassthroughFrame()
    {
        if (_cameraTexture == null) return;

        // GPU blit camera texture -> RenderTexture
        Graphics.Blit(_cameraTexture, _rt);

        // CPU readback
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readTex.ReadPixels(
            new UnityEngine.Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
        _readTex.Apply(false, false);
        RenderTexture.active = prev;

        // Convert to OpenCV Mat
        Mat frameMat = new Mat(CaptureHeight, CaptureWidth, CvType.CV_8UC3);
        OpenCVMatUtils.Texture2DToMat(_readTex, frameMat);

        // Grayscale
        Mat frameGray = new Mat();
        Imgproc.cvtColor(frameMat, frameGray, Imgproc.COLOR_RGB2GRAY);

        // Detect keypoints
        MatOfKeyPoint frameKeypoints = new MatOfKeyPoint();
        Mat frameDescriptors = new Mat();
        _detector.detectAndCompute(
            frameGray, new Mat(), frameKeypoints, frameDescriptors);

        bool detected = false;
        List<Point> detectedCorners = null;

        if (!frameDescriptors.empty() && frameKeypoints.total() > 0)
        {
            // KNN match
            List<MatOfDMatch> knnMatches = new List<MatOfDMatch>();
            _matcher.knnMatch(_referenceDescriptors, frameDescriptors, knnMatches, 2);

            // Lowe's ratio test
            List<DMatch> goodMatches = new List<DMatch>();
            foreach (var match in knnMatches)
            {
                if (match.rows() > 1)
                {
                    DMatch[] m = match.toArray();
                    if (m[0].distance < LoweRatioThreshold * m[1].distance)
                        goodMatches.Add(m[0]);
                }
            }

            if (goodMatches.Count >= MinMatchCount)
            {
                List<KeyPoint> refKpList = _referenceKeypoints.toList();
                List<KeyPoint> frameKpList = frameKeypoints.toList();
                List<Point> refPoints = new List<Point>();
                List<Point> framePoints = new List<Point>();

                foreach (var match in goodMatches)
                {
                    refPoints.Add(refKpList[match.queryIdx].pt);
                    framePoints.Add(frameKpList[match.trainIdx].pt);
                }

                MatOfPoint2f refMat2f = new MatOfPoint2f(refPoints.ToArray());
                MatOfPoint2f frameMat2f = new MatOfPoint2f(framePoints.ToArray());
                Mat H = Calib3d.findHomography(
                    refMat2f, frameMat2f, Calib3d.RANSAC, 3.0);

                if (H != null && !H.empty())
                {
                    List<Point> refCorners = new List<Point>
                    {
                        new Point(0, 0),
                        new Point(_referenceMat.cols(), 0),
                        new Point(_referenceMat.cols(), _referenceMat.rows()),
                        new Point(0, _referenceMat.rows())
                    };

                    MatOfPoint2f refCornersMat = new MatOfPoint2f(refCorners.ToArray());
                    MatOfPoint2f sceneCornersMat = new MatOfPoint2f();
                    Core.perspectiveTransform(refCornersMat, sceneCornersMat, H);
                    detectedCorners = new List<Point>(sceneCornersMat.toList());
                    detected = IsHomographyValid(detectedCorners);

                    H.Dispose();
                    refCornersMat.Dispose();
                    sceneCornersMat.Dispose();
                }

                refMat2f.Dispose();
                frameMat2f.Dispose();
            }

            foreach (var m in knnMatches) m.Dispose();
        }

        // Handle detection result
        if (detected && detectedCorners != null)
        {
            ComputeWorldPose(detectedCorners,
                out Vector3 worldCenter,
                out Quaternion worldRotation,
                out Vector3[] worldCorners);

    Debug.Log($"[QuestImageDetector] WorldCenter: {worldCenter}");
    Debug.Log($"[QuestImageDetector] WorldCorners: {worldCorners[0]}, {worldCorners[1]}, {worldCorners[2]}, {worldCorners[3]}");
    Debug.Log($"[QuestImageDetector] OutlineRenderer null? {OutlineRenderer == null}");
    Debug.Log($"[QuestImageDetector] SignMenu null? {SignMenu == null}");

            DrawWorldOutline(worldCorners);

            if (!_isDetected)
            {
                _isDetected = true;
                Debug.Log("[QuestImageDetector] Image DETECTED!");
                OnImageDetected?.Invoke();
            }

            if (SignMenu != null)
                SignMenu.OnSignDetected(worldCenter, worldRotation);
        }
        else
        {
            if (OutlineRenderer != null)
                OutlineRenderer.enabled = false;

            if (_isDetected)
            {
                _isDetected = false;
                Debug.Log("[QuestImageDetector] Image LOST.");
                OnImageLost?.Invoke();
                if (SignMenu != null)
                    SignMenu.OnSignLost();
            }
        }

        // Cleanup
        frameMat.Dispose();
        frameGray.Dispose();
        frameKeypoints.Dispose();
        frameDescriptors.Dispose();
    }

    // -------------------------------------------------------------------------
    // World Pose
    // -------------------------------------------------------------------------

    // private void ComputeWorldPose(
    //     List<Point> corners,
    //     out Vector3 worldCenter,
    //     out Quaternion worldRotation,
    //     out Vector3[] worldCorners)
    // {
    //     worldCorners = new Vector3[4];
    //     for (int i = 0; i < 4; i++)
    //         worldCorners[i] = CornerToWorld(corners[i]);

    //     worldCenter = (worldCorners[0] + worldCorners[1] +
    //                    worldCorners[2] + worldCorners[3]) / 4f;


    //     worldCenter += XRCamera.transform.TransformDirection(PositionOffset);

    //     Vector3 right = (worldCorners[1] - worldCorners[0]).normalized;
    //     Vector3 up = -(worldCorners[3] - worldCorners[0]).normalized;

    //     if (right == Vector3.zero || up == Vector3.zero)
    //     {
    //         worldRotation = XRCamera.transform.rotation;
    //         return;
    //     }

    //     Vector3 forward = Vector3.Cross(right, up).normalized;
    //     worldRotation = Quaternion.LookRotation(forward, up);
    // }

    private void ComputeWorldPose(
    List<Point> corners,
    out Vector3 worldCenter,
    out Quaternion worldRotation,
    out Vector3[] worldCorners)
    {
        worldCorners = new Vector3[4];
        for (int i = 0; i < 4; i++)
            worldCorners[i] = CornerToWorld(corners[i]);

        // Apply position correction to ALL corners first
        Vector3 correction = XRCamera.transform.TransformDirection(PositionOffset);
        for (int i = 0; i < 4; i++)
            worldCorners[i] += correction;

        // Then compute center from corrected corners
        worldCenter = (worldCorners[0] + worldCorners[1] +
                    worldCorners[2] + worldCorners[3]) / 4f;

        // Compute orientation from corrected corners
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
        float sx = ((float)corner.x / CaptureWidth) * Screen.width;
        float sy = (1f - (float)corner.y / CaptureHeight) * Screen.height;
        return XRCamera.ScreenToWorldPoint(
            new Vector3(sx, sy, EstimatedSignDistance));
    }

    // -------------------------------------------------------------------------
    // World Space Outline
    // -------------------------------------------------------------------------

    private void SetupLineRenderer()
    {
        if (OutlineRenderer == null) return;

        OutlineRenderer.positionCount = 5;
        OutlineRenderer.loop = false;
        OutlineRenderer.startWidth = OutlineWidth;
        OutlineRenderer.endWidth = OutlineWidth;
        OutlineRenderer.startColor = OutlineColor;
        OutlineRenderer.endColor = OutlineColor;
        OutlineRenderer.useWorldSpace = true;

        // Don't create material via Shader.Find — assign it manually in the Inspector instead
        // Just use whatever material is already on the LineRenderer
        if (OutlineRenderer.material == null)
        {
            Debug.LogWarning("[QuestImageDetector] No material on LineRenderer. " +
                            "Please assign a material manually in the Inspector.");
        }
        else
        {
            OutlineRenderer.material.color = OutlineColor;
        }

        OutlineRenderer.enabled = false;
    }

    // private void DrawWorldOutline(Vector3[] worldCorners)
    // {
    //     if (OutlineRenderer == null) return;

    //     Vector3 toCamera = (XRCamera.transform.position - worldCorners[0]).normalized;
    //     Vector3 offset = toCamera * OutlineZOffset;

    //     OutlineRenderer.enabled = true;
    //     OutlineRenderer.SetPosition(0, worldCorners[0] + offset); // TL
    //     OutlineRenderer.SetPosition(1, worldCorners[1] + offset); // TR
    //     OutlineRenderer.SetPosition(2, worldCorners[2] + offset); // BR
    //     OutlineRenderer.SetPosition(3, worldCorners[3] + offset); // BL
    //     OutlineRenderer.SetPosition(4, worldCorners[0] + offset); // TL (closes rect)
    // }

    private void DrawWorldOutline(Vector3[] worldCorners)
    {
        if (OutlineRenderer == null) return;

        // Compute center
        Vector3 center = (worldCorners[0] + worldCorners[1] + 
                        worldCorners[2] + worldCorners[3]) / 4f;

        // Compute width and height from corner distances
        float width  = ((worldCorners[1] - worldCorners[0]).magnitude + 
                        (worldCorners[2] - worldCorners[3]).magnitude) / 2f;
        float height = ((worldCorners[3] - worldCorners[0]).magnitude + 
                        (worldCorners[2] - worldCorners[1]).magnitude) / 2f;

        // Compute clean right and up axes from camera
        Vector3 forward = (center - XRCamera.transform.position).normalized;
        Vector3 right   = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3 up      = Vector3.Cross(forward, right).normalized;

        // Build clean rectangle corners
        Vector3 tl = center + (-right * width / 2f) + (up * height / 2f);
        Vector3 tr = center + ( right * width / 2f) + (up * height / 2f);
        Vector3 br = center + ( right * width / 2f) + (-up * height / 2f);
        Vector3 bl = center + (-right * width / 2f) + (-up * height / 2f);

        Vector3 toCamera = (XRCamera.transform.position - center).normalized;
        Vector3 offset   = toCamera * OutlineZOffset;

        OutlineRenderer.enabled = true;
        OutlineRenderer.SetPosition(0, tl + offset);
        OutlineRenderer.SetPosition(1, tr + offset);
        OutlineRenderer.SetPosition(2, br + offset);
        OutlineRenderer.SetPosition(3, bl + offset);
        OutlineRenderer.SetPosition(4, tl + offset);
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
