using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

public class MjpegHttpStreamerFromRawImage : MonoBehaviour
{
    [Header("Source")]
    public RawImage sourceRawImage;   // drag your prefab’s RawImage here
    public bool flipVertical = true;  // often needed due to ReadPixels orientation

    [Header("Stream Settings")]
    public int targetWidth = 640;
    public int targetHeight = 480;
    [Range(10, 100)] public int jpegQuality = 70;
    public int maxFps = 20;

    [Header("HTTP")]
    public int port = 8080;
    public string path = "/mjpeg";

    private HttpListener _listener;
    private readonly List<Stream> _clients = new();
    private readonly object _clientsLock = new();

    private RenderTexture _rt;
    private Texture2D _cpuTex;
    private float _nextFrameTime;

    void Start()
    {
        if (sourceRawImage == null)
        {
            Debug.LogError("Assign sourceRawImage in Inspector.");
            enabled = false;
            return;
        }

        _rt = new RenderTexture(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
        _cpuTex = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);

        StartHttpServer();
    }

    void OnDestroy()
    {
        StopHttpServer();
        if (_rt != null) _rt.Release();
        Destroy(_rt);
        Destroy(_cpuTex);
    }

    void Update()
    {
        if (Time.time < _nextFrameTime) return;
        _nextFrameTime = Time.time + (1f / Mathf.Max(1, maxFps));

        var tex = sourceRawImage.texture;
        if (tex == null) return; // feed not ready yet

        // Blit UI texture -> RT (optionally flip)
        if (flipVertical)
            Graphics.Blit(tex, _rt, new Vector2(1, -1), new Vector2(0, 1));
        else
            Graphics.Blit(tex, _rt);

        // RT -> CPU
        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _cpuTex.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0, false);
        _cpuTex.Apply(false, false);
        RenderTexture.active = prev;

        byte[] jpg = ImageConversion.EncodeToJPG(_cpuTex, jpegQuality);
        BroadcastJpeg(jpg);
    }

    private void StartHttpServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}{path}/");
        try { _listener.Start(); }
        catch (Exception e)
        {
            Debug.LogError($"HTTP Listener start failed: {e}");
            return;
        }
        ThreadPool.QueueUserWorkItem(_ => AcceptLoop());
        Debug.Log($"MJPEG: http://<QUEST_IP>:{port}{path}/");
    }

    private void StopHttpServer()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }

        lock (_clientsLock)
        {
            foreach (var c in _clients) { try { c.Close(); } catch { } }
            _clients.Clear();
        }
    }

    private void AcceptLoop()
    {
        while (_listener != null && _listener.IsListening)
        {
            HttpListenerContext ctx = null;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            if (ctx == null) continue;

            if (!ctx.Request.Url.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.SendChunked = true;
            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            ctx.Response.Headers.Add("Cache-Control", "no-cache");

            Stream output = ctx.Response.OutputStream;
            lock (_clientsLock) _clients.Add(output);
        }
    }

    private void BroadcastJpeg(byte[] jpg)
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n");
        byte[] footer = System.Text.Encoding.ASCII.GetBytes("\r\n");

        lock (_clientsLock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var s = _clients[i];
                try
                {
                    s.Write(header, 0, header.Length);
                    s.Write(jpg, 0, jpg.Length);
                    s.Write(footer, 0, footer.Length);
                    s.Flush();
                }
                catch
                {
                    try { s.Close(); } catch { }
                    _clients.RemoveAt(i);
                }
            }
        }
    }
}