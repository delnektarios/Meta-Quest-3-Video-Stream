using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Experimental.Rendering;
using NativeWebSocket;

public class QuestWsJpegStreamer : MonoBehaviour
{
    [Header("Source")]
    public RawImage sourceRawImage; // RawImage that shows passthrough feed

    [Header("Encode")]
    public int width = 1280;
    public int height = 720;
    [Range(1, 30)] public int fps = 15;
    [Range(10, 95)] public int jpegQuality = 55;

    [Header("Signaling (WebSocket)")]
    public string signalingWsUrl = "ws://172.20.10.2:8080/ws";
    public string deviceId = "quest3";

    [Header("Debug")]
    public bool logFps = true;

    private WebSocket _ws;

    private RenderTexture _rt;
    private Texture2D _readTex;
    private Coroutine _streamLoop;

    // Lightweight JSON messages (server uses these to route by deviceId/role)
    [Serializable]
    private class JoinMsg
    {
        public string type = "join";
        public string role;     // "publisher"
        public string deviceId; // "quest3"
    }

    [Serializable]
    private class ReadyMsg
    {
        public string type = "ready";
    }

    private void Awake()
    {
        Application.targetFrameRate = Mathf.Clamp(fps, 10, 60);
    }

    private IEnumerator Start()
    {
        if (sourceRawImage == null)
        {
            Debug.LogError("QuestWsJpegStreamer: Assign sourceRawImage.");
            yield break;
        }

        // Wait until the passthrough feed texture is actually assigned
        while (sourceRawImage.texture == null)
            yield return null;

        // Create target RenderTexture
        _rt = new RenderTexture(width, height, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();

        // Create CPU readback texture (RGB24 is smaller/faster for JPG)
        _readTex = new Texture2D(width, height, TextureFormat.RGB24, false);

        // Connect WS
        yield return StartCoroutine(ConnectWebSocket());

        // Start streaming frames
        _streamLoop = StartCoroutine(StreamFramesLoop());
    }

    private void Update()
    {
        // NativeWebSocket requires dispatching on main thread
        _ws?.DispatchMessageQueue();
    }

    private void OnDestroy()
    {
        if (_streamLoop != null)
            StopCoroutine(_streamLoop);

        try
        {
            if (_ws != null)
            {
                _ws.Close();
                _ws = null;
            }
        }
        catch { /* ignore */ }

        if (_rt != null)
        {
            _rt.Release();
            _rt = null;
        }

        if (_readTex != null)
        {
            Destroy(_readTex);
            _readTex = null;
        }
    }

    private IEnumerator ConnectWebSocket()
    {
        _ws = new WebSocket(signalingWsUrl);

        _ws.OnOpen += () =>
        {
            Debug.Log("WS open. Joining as publisher...");
            var join = new JoinMsg { role = "publisher", deviceId = deviceId };
            _ws.SendText(JsonUtility.ToJson(join));
        };

        _ws.OnError += (e) => Debug.LogError("WS error: " + e);

        _ws.OnClose += (e) => Debug.Log("WS closed: " + e);

        _ws.OnMessage += (bytes) =>
        {
            // We don't need to receive binary frames on publisher.
            // We do receive small text messages (peer-status, ready, etc.)
            var s = System.Text.Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrEmpty(s))
            {
                if (s.Contains("\"type\":\"ready\""))
                {
                    // Viewer indicates it is connected; optional to log.
                    Debug.Log("Viewer is ready.");
                    // You could choose to only stream when viewer ready.
                }
                else
                {
                    Debug.Log("WS msg: " + s);
                }
            }
        };

        // Connect (NativeWebSocket returns an IEnumerator-friendly Task)
        yield return _ws.Connect();

        // Wait until open or error/close
        float timeout = 8f;
        float t0 = Time.time;
        while (_ws.State == WebSocketState.Connecting && Time.time - t0 < timeout)
            yield return null;

        if (_ws.State != WebSocketState.Open)
        {
            Debug.LogError("WS did not open. State=" + _ws.State);
            yield break;
        }

        // Optional: tell the other peer we're live
        _ws.SendText(JsonUtility.ToJson(new ReadyMsg()));
    }

    private IEnumerator StreamFramesLoop()
    {
        // Throttle
        float interval = 1f / Mathf.Max(1, fps);

        float targetDt = 1f / Mathf.Max(1, fps);
        float nextTime = Time.realtimeSinceStartup;

        // FPS stats
        int sentFrames = 0;
        float statT0 = Time.time;

        var waitEOF = new WaitForEndOfFrame();

        while (true)
        {
            // If WS is down, just idle
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                yield return new WaitForSeconds(0.25f);
                continue;
            }

            // If behind, skip ahead (prevents latency buildup)
            float now = Time.realtimeSinceStartup;
            if (now < nextTime)
            {
                yield return null;
                continue;
            }
            nextTime = now + targetDt;

            // Wait until end of frame so UI texture is up to date
            yield return waitEOF;

            // 1) Blit RawImage texture -> RenderTexture (GPU)
            var src = sourceRawImage.texture;
            if (src == null)
            {
                yield return new WaitForSeconds(interval);
                continue;
            }

            Graphics.Blit(src, _rt);

            // 2) Readback RT -> Texture2D (CPU)
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;

            // Note: ReadPixels is expensive; keep resolution/fps modest.
            _readTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            _readTex.Apply(false, false);

            RenderTexture.active = prev;

            // 3) Encode JPEG
            byte[] jpg = _readTex.EncodeToJPG(jpegQuality);

            // 4) Send as binary WS message
            _ws.Send(jpg);

            // Stats
            sentFrames++;
            if (logFps && Time.time - statT0 >= 1.0f)
            {
                float dt = Time.time - statT0;
                float effFps = sentFrames / dt;
                Debug.Log($"WS stream: fps={effFps:F1} jpg_bytes≈{(jpg != null ? jpg.Length : 0)} res={width}x{height} q={jpegQuality}");
                sentFrames = 0;
                statT0 = Time.time;
            }

            yield return new WaitForSeconds(interval);
        }
    }
}