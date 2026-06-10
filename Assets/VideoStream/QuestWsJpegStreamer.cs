using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Experimental.Rendering;
using NativeWebSocket;

/// <summary>
/// QuestWsJpegStreamer
/// Captures the passthrough camera feed from a RawImage,
/// encodes each frame as JPEG, and streams it over WebSocket
/// to the Python relay server (server.py).
///
/// SETUP:
///   1. Assign sourceRawImage — the RawImage showing passthrough feed
///   2. Set signalingWsUrl to your server IP e.g. ws://192.168.1.x:8080/ws
///   3. Open http://your-server-ip:8080 in browser to view stream
/// </summary>
public class QuestWsJpegStreamer : MonoBehaviour
{
    [Header("Source")]
    public RawImage sourceRawImage;

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

    [Serializable]
    private class JoinMsg
    {
        public string type = "join";
        public string role;
        public string deviceId;
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

        while (sourceRawImage.texture == null)
            yield return null;

        _rt = new RenderTexture(width, height, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();
        _readTex = new Texture2D(width, height, TextureFormat.RGB24, false);

        yield return StartCoroutine(ConnectWebSocket());
        _streamLoop = StartCoroutine(StreamFramesLoop());
    }

    private void Update()
    {
        _ws?.DispatchMessageQueue();
    }

    private void OnDestroy()
    {
        if (_streamLoop != null) StopCoroutine(_streamLoop);
        try { _ws?.Close(); _ws = null; } catch { }
        if (_rt != null) { _rt.Release(); _rt = null; }
        if (_readTex != null) { Destroy(_readTex); _readTex = null; }
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
            var s = System.Text.Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrEmpty(s))
            {
                if (s.Contains("\"type\":\"ready\""))
                    Debug.Log("Viewer is ready.");
                else
                    Debug.Log("WS msg: " + s);
            }
        };

        yield return _ws.Connect();

        float timeout = 8f;
        float t0 = Time.time;
        while (_ws.State == WebSocketState.Connecting && Time.time - t0 < timeout)
            yield return null;

        if (_ws.State != WebSocketState.Open)
        {
            Debug.LogError("WS did not open. State=" + _ws.State);
            yield break;
        }

        _ws.SendText(JsonUtility.ToJson(new ReadyMsg()));
    }

    private IEnumerator StreamFramesLoop()
    {
        float targetDt = 1f / Mathf.Max(1, fps);
        float nextTime = Time.realtimeSinceStartup;
        int sentFrames = 0;
        float statT0 = Time.time;
        var waitEOF = new WaitForEndOfFrame();

        while (true)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                yield return new WaitForSeconds(0.25f);
                continue;
            }

            float now = Time.realtimeSinceStartup;
            if (now < nextTime) { yield return null; continue; }
            nextTime = now + targetDt;

            yield return waitEOF;

            var src = sourceRawImage.texture;
            if (src == null) { yield return new WaitForSeconds(1f / fps); continue; }

            Graphics.Blit(src, _rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _readTex.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0, false);
            _readTex.Apply(false, false);
            RenderTexture.active = prev;

            byte[] jpg = _readTex.EncodeToJPG(jpegQuality);
            _ws.Send(jpg);

            sentFrames++;
            if (logFps && Time.time - statT0 >= 1.0f)
            {
                float dt = Time.time - statT0;
                Debug.Log($"WS stream: fps={sentFrames / dt:F1} " +
                          $"jpg_bytes={jpg?.Length} res={width}x{height} q={jpegQuality}");
                sentFrames = 0;
                statT0 = Time.time;
            }

            yield return new WaitForSeconds(1f / fps);
        }
    }
}
