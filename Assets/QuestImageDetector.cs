using System.Collections;
using System.Collections.Generic;
using System.Threading;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Features2dModule;
using OpenCVForUnity.ImgcodecsModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;

/// <summary>
/// QuestImageDetector (v3 — World Space Outline)
/// Detects a reference image in the Quest 3 passthrough camera feed using
/// SIFT + Homography (OpenCV for Unity).
///
/// On detection:
///   - Draws a green rectangle in WORLD SPACE over the physical sign using a LineRenderer
///   - Positions and shows the SignMenuCanvas in world space over the sign
///   - Fires OnImageDetected / OnImageLost events
///
/// The passthrough feed RawImage is used invisibly (alpha = 0) for pixel capture only.
/// No 2D overlay texture is used.
/// </summary>
public class QuestImageDetector : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Passthrough Feed (set alpha=0 on the RawImage to hide it)")]
    [Tooltip("The RawImage connected to the passthrough feed. Used invisibly for detection only.")]
    public RawImage SourceRawImage;

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

    [Header("World Space")]
    [Tooltip("The Quest 3 center eye camera. Assign CenterEyeAnchor or leave empty for Camera.main.")]
    public Camera XRCamera;

    [Tooltip("Estimated distance in meters from camera to sign.")]
    [Range(0.2f, 5f)]
    public float EstimatedSignDistance = 1.0f;

    [Header("World Space Outline")]
    [Tooltip("LineRenderer used to draw the green rectangle over the physical sign in 3D.")]
    public LineRenderer OutlineRenderer;

    [Tooltip("Color of the outline rectangle.")]
    public Color OutlineColor = Color.green;

    [Tooltip("Width of the outline in meters.")]
    [Range(0.001f, 0.02f)]
    public float OutlineWidth = 0.005f;

    [Tooltip("How far in front of the sign surface to offset the outline (avoids z-fighting).")]
    [Range(0f, 0.05f)]
    public float OutlineZOffset = 0.01f;

    [Header("Sign Menu")]
    [Tooltip("Assign the SignMenuController component here.")]
    public SignMenuController SignMenu;

    [Header("Events")]
    public UnityEvent OnImageDetected;
    public UnityEvent OnImageLost;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private Mat _referenceMat;
    private MatOfKeyPoint _referenceKeypoints;
    private Mat _referenceDescriptors;
    private SIFT _detector;
    private DescriptorMatcher _matcher;

    private RenderTexture _rt;
    private Texture2D _readTex;

    private bool _initialized = false;
    private bool _isDetected = false;
    private int _frameCounter = 0;
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private IEnumerator Start()
    {
        if (SourceRawImage == null)
        {
            Debug.LogError("[QuestImageDetector] SourceRawImage is not assigned.");
            yield break;
        }

        if (XRCamera == null)
            XRCamera = Camera.main;

        // Setup LineRenderer
        if (OutlineRenderer != null)
        {
            OutlineRenderer.positionCount = 5; // 4 corners + closing point
            OutlineRenderer.loop = false;
            OutlineRenderer.startWidth = OutlineWidth;
            OutlineRenderer.endWidth = OutlineWidth;
            OutlineRenderer.startColor = OutlineColor;
            OutlineRenderer.endColor = OutlineColor;
            OutlineRenderer.useWorldSpace = true;

            // Use Unlit material so it's always visible in passthrough
            OutlineRenderer.material = new Material(Shader.Find("Unlit/Color"));
            OutlineRenderer.material.color = OutlineColor;
            OutlineRenderer.enabled = false; // hidden until detection
        }

        // Wait for passthrough feed texture
        Debug.Log("[QuestImageDetector] Waiting for passthrough feed...");
        while (SourceRawImage.texture == null)
            yield return null;

        Debug.Log("[QuestImageDetector] Passthrough feed ready.");

        // Capture textures
        _rt = new RenderTexture(CaptureWidth, CaptureHeight, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();
        _readTex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

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
            Debug.LogError($"[QuestImageDetector] Reference image not found: StreamingAssets/{ReferenceImagePath}");
            yield break;
        }

        _referenceMat = Imgcodecs.imread(filepath, Imgcodecs.IMREAD_GRAYSCALE);

        if (_referenceMat.empty())
        {
            Debug.LogError($"[QuestImageDetector] Could not read image: {filepath}");
            yield break;
        }

        _detector = SIFT.create();
        _matcher = DescriptorMatcher.create(DescriptorMatcher.FLANNBASED);
        _referenceKeypoints = new MatOfKeyPoint();
        _referenceDescriptors = new Mat();
        _detector.detectAndCompute(_referenceMat, new Mat(), _referenceKeypoints, _referenceDescriptors);

        Debug.Log($"[QuestImageDetector] Ready. Keypoints: {_referenceKeypoints.total()}");
        _initialized = true;
    }

    private void Update()
    {
        if (!_initialized) return;
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
        var src = SourceRawImage.texture;
        if (src == null) return;

        // GPU blit -> CPU readback
        Graphics.Blit(src, _rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readTex.ReadPixels(new UnityEngine.Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
        _readTex.Apply(false, false);
        RenderTexture.active = prev;

        Mat frameMat = new Mat(CaptureHeight, CaptureWidth, CvType.CV_8UC3);
        OpenCVMatUtils.Texture2DToMat(_readTex, frameMat);

        Mat frameGray = new Mat();
        Imgproc.cvtColor(frameMat, frameGray, Imgproc.COLOR_RGB2GRAY);

        MatOfKeyPoint frameKeypoints = new MatOfKeyPoint();
        Mat frameDescriptors = new Mat();
        _detector.detectAndCompute(frameGray, new Mat(), frameKeypoints, frameDescriptors);

        bool detected = false;
        List<Point> detectedCorners = null;

        if (!frameDescriptors.empty() && frameKeypoints.total() > 0)
        {
            List<MatOfDMatch> knnMatches = new List<MatOfDMatch>();
            _matcher.knnMatch(_referenceDescriptors, frameDescriptors, knnMatches, 2);

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
                Mat H = Calib3d.findHomography(refMat2f, frameMat2f, Calib3d.RANSAC, 3.0);

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

        if (detected && detectedCorners != null)
        {
            // Compute world pose
            ComputeWorldPose(detectedCorners,
                out Vector3 worldCenter,
                out Quaternion worldRotation,
                out Vector3[] worldCorners);

            // Draw world space outline
            DrawWorldOutline(worldCorners, worldRotation);

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
            // Hide outline
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

        frameMat.Dispose();
        frameGray.Dispose();
        frameKeypoints.Dispose();
        frameDescriptors.Dispose();
    }

    // -------------------------------------------------------------------------
    // World Pose + Outline
    // -------------------------------------------------------------------------

    private void ComputeWorldPose(
        List<Point> corners,
        out Vector3 worldCenter,
        out Quaternion worldRotation,
        out Vector3[] worldCorners)
    {
        // Convert all 4 corners to world space
        worldCorners = new Vector3[4];
        for (int i = 0; i < 4; i++)
            worldCorners[i] = CornerToWorld(corners[i]);

        // Center = average of 4 world corners
        worldCenter = (worldCorners[0] + worldCorners[1] + worldCorners[2] + worldCorners[3]) / 4f;

        // Orientation from corner vectors
        Vector3 right = (worldCorners[1] - worldCorners[0]).normalized; // TL -> TR
        Vector3 up = -(worldCorners[3] - worldCorners[0]).normalized;   // TL -> BL (inverted Y)

        if (right == Vector3.zero || up == Vector3.zero)
        {
            worldRotation = XRCamera.transform.rotation;
            return;
        }

        Vector3 forward = Vector3.Cross(right, up).normalized;
        worldRotation = Quaternion.LookRotation(forward, up);
    }

    /// <summary>
    /// Draws the green rectangle in world space using the LineRenderer.
    /// Offsets slightly toward the camera to appear in front of the sign surface.
    /// </summary>
    private void DrawWorldOutline(Vector3[] worldCorners, Quaternion worldRotation)
    {
        if (OutlineRenderer == null) return;

        // Offset corners slightly toward camera to avoid z-fighting with the sign
        Vector3 toCamera = (XRCamera.transform.position - worldCorners[0]).normalized;
        Vector3 offset = toCamera * OutlineZOffset;

        OutlineRenderer.enabled = true;
        OutlineRenderer.SetPosition(0, worldCorners[0] + offset); // TL
        OutlineRenderer.SetPosition(1, worldCorners[1] + offset); // TR
        OutlineRenderer.SetPosition(2, worldCorners[2] + offset); // BR
        OutlineRenderer.SetPosition(3, worldCorners[3] + offset); // BL
        OutlineRenderer.SetPosition(4, worldCorners[0] + offset); // TL again (closes the rect)
    }

    private Vector3 CornerToWorld(Point corner)
    {
        float sx = ((float)corner.x / CaptureWidth) * Screen.width;
        float sy = (1f - (float)corner.y / CaptureHeight) * Screen.height;
        return XRCamera.ScreenToWorldPoint(new Vector3(sx, sy, EstimatedSignDistance));
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
