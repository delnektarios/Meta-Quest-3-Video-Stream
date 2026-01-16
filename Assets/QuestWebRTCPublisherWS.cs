using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Unity.WebRTC;
using UnityEngine.Experimental.Rendering;
using NativeWebSocket;

public class QuestWebRTCPublisherWS : MonoBehaviour
{
    [Header("Source")]
    public RawImage sourceRawImage;

    [Header("Video")]
    public int width = 640;
    public int height = 480;
    public int fps = 20;

    [Header("Signaling (WebSocket)")]
    public string signalingWsUrl = "ws://172.20.10.2:8080/ws"; // server IP
    public string deviceId = "quest3";

    private RTCPeerConnection _pc;
    private VideoStreamTrack _videoTrack;
    private RenderTexture _rt;

    private WebSocket _ws;

    [Serializable] private class JoinMsg { public string type="join"; public string role; public string deviceId; }
    [Serializable] private class SdpMsg { public string type; public string sdp; }
    [Serializable] private class CandidateMsg { public string type="candidate"; public string candidate; public string sdpMid; public int sdpMLineIndex; }
    [Serializable] private class PeerStatusMsg { public string type; public string peer; public string status; public string deviceId; }

    private void Awake()
    {
        Application.targetFrameRate = Mathf.Clamp(fps, 10, 60);
    }

    private void OnDestroy()
    {
        try
        {
            _videoTrack?.Dispose();
            _pc?.Close();
            _pc?.Dispose();
        }
        catch { }

        if (_rt != null) _rt.Release();

        if (_ws != null)
        {
            _ws.Close();
            _ws = null;
        }

    }

    private IEnumerator Start()
    {
        if (sourceRawImage == null)
        {
            Debug.LogError("Assign sourceRawImage");
            yield break;
        }

        while (sourceRawImage.texture == null)
            yield return null;

        _rt = new RenderTexture(width, height, 0, GraphicsFormat.B8G8R8A8_SRGB);
        _rt.Create();

        StartCoroutine(FrameBlitLoop());
        StartCoroutine(WebRTC.Update());

        yield return StartCoroutine(ConnectWebSocket());
        SetupPeerConnection();

        // Create and send offer after WS is connected
        //yield return StartCoroutine(CreateAndSendOffer());
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
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            // We branch by checking for fields (JsonUtility is limited)
            if (json.Contains("\"type\":\"answer\""))
            {
                var msg = JsonUtility.FromJson<SdpMsg>(json);
                StartCoroutine(ApplyAnswer(msg.sdp));
            }
            else if (json.Contains("\"type\":\"candidate\""))
            {
                var c = JsonUtility.FromJson<CandidateMsg>(json);
                AddRemoteCandidate(c);
            }
            else if (json.Contains("\"type\":\"peer-status\""))
            {
                var ps = JsonUtility.FromJson<PeerStatusMsg>(json);
                Debug.Log($"Peer {ps.peer} is {ps.status}");
            }
            else if (json.Contains("\"type\":\"ready\""))
            {
                Debug.Log("Viewer is ready. Creating offer now...");
                StartCoroutine(CreateAndSendOffer());
            }

            else
            {
                Debug.Log("WS msg: " + json);
            }
        };

        yield return _ws.Connect();

        // NativeWebSocket requires DispatchMessageQueue on main thread
        while (_ws.State == WebSocketState.Connecting)
            yield return null;
    }

    private void Update()
    {
        _ws?.DispatchMessageQueue();
    }

    private void SetupPeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
            }
        };

        _pc = new RTCPeerConnection(ref config);

        _pc.OnIceCandidate = (cand) =>
        {
            if (cand == null) return;
            var msg = new CandidateMsg
            {
                candidate = cand.Candidate,
                sdpMid = cand.SdpMid,
                sdpMLineIndex = cand.SdpMLineIndex ?? 0
            };
            _ws.SendText(JsonUtility.ToJson(msg));
        };

        _pc.OnIceConnectionChange = state => Debug.Log("ICE: " + state);
        _pc.OnConnectionStateChange = state => Debug.Log("PC state: " + state);

        _videoTrack = new VideoStreamTrack(_rt);
        _pc.AddTrack(_videoTrack);
    }

    private IEnumerator CreateAndSendOffer()
    {
        Debug.Log("Creating offer...");
        var offerOp = _pc.CreateOffer();
        yield return offerOp;

        if (offerOp.IsError)
        {
            Debug.LogError("CreateOffer error: " + offerOp.Error.message);
            yield break;
        }

        var desc = offerOp.Desc;
        var setLocalOp = _pc.SetLocalDescription(ref desc);
        yield return setLocalOp;

        if (setLocalOp.IsError)
        {
            Debug.LogError("SetLocalDescription error: " + setLocalOp.Error.message);
            yield break;
        }

        // Send SDP offer immediately; candidates will be trickled via OnIceCandidate
        var offer = new SdpMsg { type = "offer", sdp = _pc.LocalDescription.sdp };
        _ws.SendText(JsonUtility.ToJson(offer));
        Debug.Log("Offer sent (trickle ICE running).");
    }

    private IEnumerator ApplyAnswer(string sdp)
    {
        Debug.Log("Applying answer...");
        var answerDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
        var op = _pc.SetRemoteDescription(ref answerDesc);
        yield return op;

        if (op.IsError)
            Debug.LogError("SetRemoteDescription error: " + op.Error.message);
        else
            Debug.Log("Answer applied.");
    }

    private void AddRemoteCandidate(CandidateMsg c)
    {
        try
        {
            var init = new RTCIceCandidateInit
            {
                candidate = c.candidate,
                sdpMid = c.sdpMid,
                sdpMLineIndex = c.sdpMLineIndex
            };
            var cand = new RTCIceCandidate(init);
            _pc.AddIceCandidate(cand);
        }
        catch (Exception e)
        {
            Debug.LogError("AddIceCandidate exception: " + e);
        }
    }

    private IEnumerator FrameBlitLoop()
    {
        // Simple stable pump
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            var src = sourceRawImage.texture;
            if (src != null && _rt != null)
                Graphics.Blit(src, _rt);
        }
    }
}
