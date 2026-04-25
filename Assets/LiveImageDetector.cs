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
using UnityEngine.UI;

/// <summary>
/// LiveImageDetector
/// Detects a custom reference image (e.g. a printed sign or card) in a live
/// webcam feed using SIFT feature matching + Homography.
///
/// SETUP:
///   1. Attach this script to a GameObject in your scene.
///   2. Place your reference image in:
///      Assets/StreamingAssets/YourFolder/yourimage.png
///      and set ReferenceImagePath accordingly.
///   3. Assign a RawImage UI element to PreviewImage to see the camera feed.
///   4. Assign the UI GameObject to show/hide via OnImageDetected / OnImageLost.
///
/// FOR QUEST 3:
///   Replace WebCamTexture with your PassthroughCameraAccess WebCamTexture
///   reference from the MRUK PassthroughCameraAccess component.
/// </summary>
public class LiveImageDetector : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector Fields
    // -------------------------------------------------------------------------

    [Header("Reference Image")]
    [Tooltip("Path relative to StreamingAssets folder. E.g: OpenCVForUnityExamples/features2d/firehose.png")]
    public string ReferenceImagePath = "OpenCVForUnityExamples/features2d/firehose.png";

    [Header("Camera")]
    [Tooltip("Leave empty to use the default webcam. For Quest 3, assign the PassthroughCameraAccess WebCamTexture here at runtime.")]
    public string WebCamDeviceName = "";

    [Tooltip("Requested camera width")]
    public int RequestedWidth = 1280;

    [Tooltip("Requested camera height")]
    public int RequestedHeight = 720;

    [Header("Detection Settings")]
    [Tooltip("Minimum number of good matches to consider the image detected. Lower = more sensitive, Higher = less false positives.")]
    [Range(5, 50)]
    public int MinMatchCount = 12;

    [Tooltip("How many frames to skip between detections. 3 = process every 3rd frame. Higher = better performance.")]
    [Range(1, 10)]
    public int ProcessEveryNFrames = 3;

    [Tooltip("Lowe's ratio test threshold. Lower = stricter matching.")]
    [Range(0.5f, 0.9f)]
    public float LoweRatioThreshold = 0.75f;

    [Header("UI")]
    [Tooltip("RawImage to display the camera feed with detection overlay.")]
    public RawImage PreviewImage;

    [Tooltip("Optional: GameObject to show when image is detected (e.g. your UI panel).")]
    public GameObject DetectedUI;

    [Header("Events")]
    [Tooltip("Fired when the target image is first detected.")]
    public UnityEvent OnImageDetected;

    [Tooltip("Fired when the target image is lost.")]
    public UnityEvent OnImageLost;

    // -------------------------------------------------------------------------
    // Private Fields
    // -------------------------------------------------------------------------

    private WebCamTexture _webCamTexture;
    private Mat _referenceMat;
    private MatOfKeyPoint _referenceKeypoints;
    private Mat _referenceDescriptors;
    private SIFT _detector;
    private DescriptorMatcher _matcher;

    private bool _initialized = false;
    private bool _isDetected = false;
    private int _frameCounter = 0;

    private Texture2D _outputTexture;
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // -------------------------------------------------------------------------
    // Unity Lifecycle
    // -------------------------------------------------------------------------

    private async void Start()
    {
        // Load reference image from StreamingAssets
        string filepath = await OpenCVEnv.GetFilePathTaskAsync(ReferenceImagePath, cancellationToken: _cts.Token);

        if (string.IsNullOrEmpty(filepath))
        {
            Debug.LogError($"[LiveImageDetector] Reference image not found at StreamingAssets/{ReferenceImagePath}. " +
                           "Make sure the file exists in your StreamingAssets folder.");
            return;
        }

        // Load and process reference image
        _referenceMat = Imgcodecs.imread(filepath, Imgcodecs.IMREAD_GRAYSCALE);

        if (_referenceMat.empty())
        {
            Debug.LogError($"[LiveImageDetector] Failed to load reference image from: {filepath}");
            return;
        }

        // Initialize SIFT detector
        _detector = SIFT.create();
        _matcher = DescriptorMatcher.create(DescriptorMatcher.FLANNBASED);

        // Extract features from reference image (done once)
        _referenceKeypoints = new MatOfKeyPoint();
        _referenceDescriptors = new Mat();
        _detector.detectAndCompute(_referenceMat, new Mat(), _referenceKeypoints, _referenceDescriptors);

        Debug.Log($"[LiveImageDetector] Reference image loaded with {_referenceKeypoints.total()} keypoints.");

        // Start webcam
        StartCoroutine(InitializeWebCam());
    }

    private IEnumerator InitializeWebCam()
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("[LiveImageDetector] Camera permission denied.");
            yield break;
        }

        // Select camera device
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("[LiveImageDetector] No webcam devices found.");
            yield break;
        }

        string deviceName = WebCamDeviceName;
        if (string.IsNullOrEmpty(deviceName))
            deviceName = devices[0].name;

        _webCamTexture = new WebCamTexture(deviceName, RequestedWidth, RequestedHeight);
        _webCamTexture.Play();

        // Wait for camera to initialize
        int timeout = 0;
        while (_webCamTexture.width <= 16 && timeout < 300)
        {
            timeout++;
            yield return null;
        }

        if (_webCamTexture.width <= 16)
        {
            Debug.LogError("[LiveImageDetector] Camera failed to initialize.");
            yield break;
        }

        // Create output texture for preview
        _outputTexture = new Texture2D(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGB24, false);

        if (PreviewImage != null)
        {
            PreviewImage.texture = _outputTexture;
            var fitter = PreviewImage.GetComponent<AspectRatioFitter>();
            if (fitter != null) fitter.aspectRatio = (float)_webCamTexture.width / _webCamTexture.height;        
        }
    
        _initialized = true;
        Debug.Log($"[LiveImageDetector] Camera started: {_webCamTexture.width}x{_webCamTexture.height}");
    }

    private void Update()
    {
        if (!_initialized || _webCamTexture == null || !_webCamTexture.isPlaying)
            return;

        // Skip frames for performance
        _frameCounter++;
        if (_frameCounter % ProcessEveryNFrames != 0)
            return;

        DetectInFrame();
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        _webCamTexture?.Stop();

        _referenceMat?.Dispose();
        _referenceDescriptors?.Dispose();
        _referenceKeypoints?.Dispose();
        _detector?.Dispose();
        _matcher?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Detection
    // -------------------------------------------------------------------------

    private void DetectInFrame()
    {
        // Convert webcam frame to OpenCV Mat
        Mat frameMat = new Mat(_webCamTexture.height, _webCamTexture.width, CvType.CV_8UC4);
        OpenCVMatUtils.WebCamTextureToMat(_webCamTexture, frameMat);

        // Convert to grayscale for detection
        Mat frameGray = new Mat();
        Imgproc.cvtColor(frameMat, frameGray, Imgproc.COLOR_RGBA2GRAY);

        // Detect keypoints in current frame
        MatOfKeyPoint frameKeypoints = new MatOfKeyPoint();
        Mat frameDescriptors = new Mat();
        _detector.detectAndCompute(frameGray, new Mat(), frameKeypoints, frameDescriptors);

        bool detected = false;
        List<Point> detectedCorners = null;

        if (!frameDescriptors.empty() && frameKeypoints.total() > 0)
        {
            // KNN match descriptors
            List<MatOfDMatch> knnMatches = new List<MatOfDMatch>();
            _matcher.knnMatch(_referenceDescriptors, frameDescriptors, knnMatches, 2);

            // Lowe's ratio test to filter good matches
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
                // Find homography to locate the object in the scene
                List<KeyPoint> refKpList = _referenceKeypoints.toList();
                List<KeyPoint> frameKpList = frameKeypoints.toList();

                List<Point> refPoints = new List<Point>();
                List<Point> framePoints = new List<Point>();

                foreach (var match in goodMatches)
                {
                    refPoints.Add(refKpList[match.queryIdx].pt);
                    framePoints.Add(frameKpList[match.trainIdx].pt);
                }

                MatOfPoint2f refMat = new MatOfPoint2f(refPoints.ToArray());
                MatOfPoint2f frameMat2f = new MatOfPoint2f(framePoints.ToArray());

                Mat H = Calib3d.findHomography(refMat, frameMat2f, Calib3d.RANSAC, 3.0);

                if (H != null && !H.empty())
                {
                    // Map reference image corners to scene
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
                    refMat.Dispose();
                    frameMat2f.Dispose();
                }
            }

            // Cleanup match data
            foreach (var m in knnMatches) m.Dispose();
        }

        // Draw overlay on the frame
        if (detected && detectedCorners != null)
        {
            DrawDetectionOverlay(frameMat, detectedCorners);
        }

        // Update preview texture
        OpenCVMatUtils.MatToTexture2D(frameMat, _outputTexture);

        // Fire events on state change
        if (detected && !_isDetected)
        {
            _isDetected = true;
            Debug.Log("[LiveImageDetector] Image DETECTED!");
            if (DetectedUI != null) DetectedUI.SetActive(true);
            OnImageDetected?.Invoke();
        }
        else if (!detected && _isDetected)
        {
            _isDetected = false;
            Debug.Log("[LiveImageDetector] Image LOST.");
            if (DetectedUI != null) DetectedUI.SetActive(false);
            OnImageLost?.Invoke();
        }

        // Cleanup
        frameMat.Dispose();
        frameGray.Dispose();
        frameKeypoints.Dispose();
        frameDescriptors.Dispose();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates that the detected homography corners form a reasonable quadrilateral.
    /// Rejects detections that are too small, too large, or non-convex.
    /// </summary>
    private bool IsHomographyValid(List<Point> corners)
    {
        if (corners == null || corners.Count != 4)
            return false;

        // Check that the polygon is convex (rules out twisted/invalid homographies)
        MatOfPoint2f cornersMat = new MatOfPoint2f(corners.ToArray());
        MatOfPoint cornersInt = new MatOfPoint();
        cornersMat.convertTo(cornersInt, CvType.CV_32S);

        bool isConvex = Imgproc.isContourConvex(cornersInt);

        cornersMat.Dispose();
        cornersInt.Dispose();

        if (!isConvex)
            return false;

        // Check that the detected area is a reasonable size
        double area = Imgproc.contourArea(new MatOfPoint2f(corners.ToArray()));
        double frameArea = _webCamTexture.width * _webCamTexture.height;

        // Reject if too small (< 0.5% of frame) or too large (> 80% of frame)
        if (area < frameArea * 0.005 || area > frameArea * 0.8)
            return false;

        return true;
    }

    /// <summary>
    /// Draws a green outline around the detected image in the frame.
    /// </summary>
    private void DrawDetectionOverlay(Mat frame, List<Point> corners)
    {
        Scalar green = new Scalar(0, 255, 0, 255);
        int thickness = 4;

        Imgproc.line(frame, corners[0], corners[1], green, thickness);
        Imgproc.line(frame, corners[1], corners[2], green, thickness);
        Imgproc.line(frame, corners[2], corners[3], green, thickness);
        Imgproc.line(frame, corners[3], corners[0], green, thickness);

        // Draw "DETECTED" label
        Point labelPos = new Point(corners[0].x, corners[0].y - 10);
        Imgproc.putText(frame, "DETECTED", labelPos,
            Imgproc.FONT_HERSHEY_SIMPLEX, 1.0,
            new Scalar(0, 255, 0, 255), 2, Imgproc.LINE_AA, false);
    }
}