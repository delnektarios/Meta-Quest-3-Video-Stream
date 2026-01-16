using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Unity.WebRTC;
using UnityEngine.Experimental.Rendering;


public class QuestWebRTCPublisher : MonoBehaviour
{
    [Header("Source")]
    public RawImage sourceRawImage;   // the RawImage that already shows the passthrough feed

    [Header("Video")]
    public int width = 640;
    public int height = 480;
    public int fps = 20;

    [Header("Signaling")]
    public string signalingBaseUrl = "http://172.20.10.2:8080"; // your Mac/server IP
    public string offerPath = "/offer"; // POST offer SDP, receive answer SDP
    public string deviceId = "quest3"; // choose an ID per device

    private RTCPeerConnection _pc;
    private VideoStreamTrack _videoTrack;
    private RenderTexture _rt;
    private bool _iceGatheringComplete;
    private int _iceCandidateCount;


    private Coroutine _webRtcUpdateLoop;

    [Serializable]
    private class SdpMessage
    {
        public string type; // "offer" or "answer"
        public string sdp;
    }

    void Awake()
    {
        Application.targetFrameRate = Mathf.Clamp(fps, 10, 60);
    }

    void OnDestroy()
    {
        //WebRTC.Dispose();
        try
        {
            _videoTrack?.Dispose();
            _pc?.Close();
            _pc?.Dispose();
        }
        catch { /* ignore */ }

        if (_rt != null)
            _rt.Release();

        if (_webRtcUpdateLoop != null)
            StopCoroutine(_webRtcUpdateLoop);
    }

    IEnumerator Start()
    {
        if (sourceRawImage == null)
        {
            Debug.LogError("QuestWebRTCPublisher: Assign sourceRawImage.");
            yield break;
        }

        // Wait until the camera feed texture is actually assigned
        while (sourceRawImage.texture == null)
            yield return null;

        _rt = new RenderTexture(
            width,
            height,
            0,
            GraphicsFormat.B8G8R8A8_SRGB
        );

        _rt.Create();

        // Create peer connection
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
                // For LAN dev, this can be empty.
                // For K8s/Internet later, you'll add STUN/TURN here.
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
            }
        };

        _pc = new RTCPeerConnection(ref config);

        // Log ICE candidates as they are gathered (works in older WebRTC versions)
        _pc.OnIceCandidate = candidate =>
        {
            if (candidate == null)
            {
                _iceGatheringComplete = true;
                Debug.Log("ICE gathering complete (null candidate).");
                return;
            }

            _iceCandidateCount++;
            Debug.Log($"ICE candidate #{_iceCandidateCount}: {candidate.Candidate}");
        };


        // Optional: log ICE state
        _pc.OnIceConnectionChange = state => Debug.Log($"ICE: {state}");
        _pc.OnConnectionStateChange = state => Debug.Log($"PC state: {state}");

        // Create video track from RenderTexture
        var transceiver = _pc.AddTransceiver(TrackKind.Video);
        transceiver.Direction = RTCRtpTransceiverDirection.SendOnly;


        // Start WebRTC update loop required by Unity.WebRTC
        //WebRTC.Initialize();
        _webRtcUpdateLoop = StartCoroutine(WebRTC.Update());

        // Start pushing frames into the RenderTexture
        StartCoroutine(FrameBlitLoop());

        // Create offer and do signaling
        yield return StartCoroutine(NegotiateWithServer());
    }

    private IEnumerator FrameBlitLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            var src = sourceRawImage.texture;
            if (src != null && _rt != null)
                Graphics.Blit(src, _rt);
        }
    }

    private IEnumerator NegotiateWithServer()
    {
        Debug.Log("Starting WebRTC negotiation...");

        _iceGatheringComplete = false;
        _iceCandidateCount = 0;

        var offerOp = _pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError($"CreateOffer error: {offerOp.Error.message}");
            yield break;
        }

        var offerDesc = offerOp.Desc;

        var setLocalOp = _pc.SetLocalDescription(ref offerDesc);
        yield return setLocalOp;
        if (setLocalOp.IsError)
        {
            Debug.LogError($"SetLocalDescription error: {setLocalOp.Error.message}");
            yield break;
        }

        // Wait for ICE gathering to complete (or timeout)
        float iceTimeout = 15f;
        float t0 = Time.time;
        while (!_iceGatheringComplete && Time.time - t0 < iceTimeout)
            yield return null;

        Debug.Log($"ICE gathered candidates={_iceCandidateCount}, complete={_iceGatheringComplete}");

        var finalOffer = _pc.LocalDescription;

        var offerMsg = new SdpMessage { type = "offer", sdp = finalOffer.sdp };
        string offerJson = JsonUtility.ToJson(offerMsg);

        string offerUrl = $"{signalingBaseUrl.TrimEnd('/')}/offer/{deviceId}";
        using (var req = new UnityWebRequest(offerUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(offerJson));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 10;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"POST offer failed: result={req.result} code={req.responseCode} err={req.error}");
                yield break;
            }

            Debug.Log($"POST offer success: {req.downloadHandler?.text}");
        }

        Debug.Log("Offer posted. Polling for answer...");

        string answerUrl = $"{signalingBaseUrl.TrimEnd('/')}/answer/{deviceId}";
        float overallTimeout = 30f;
        float startTime = Time.time;

        while (Time.time - startTime < overallTimeout)
        {
            using var get = UnityWebRequest.Get(answerUrl);
            get.timeout = 10;

            yield return get.SendWebRequest();

            if (get.result == UnityWebRequest.Result.Success && get.responseCode == 200)
            {
                var answerJson = get.downloadHandler.text;
                var answerMsg = JsonUtility.FromJson<SdpMessage>(answerJson);

                if (!string.IsNullOrEmpty(answerMsg?.sdp))
                {
                    var answerDesc = new RTCSessionDescription
                    {
                        type = RTCSdpType.Answer,
                        sdp = answerMsg.sdp
                    };

                    var setRemoteOp = _pc.SetRemoteDescription(ref answerDesc);
                    yield return setRemoteOp;

                    if (setRemoteOp.IsError)
                    {
                        Debug.LogError($"SetRemoteDescription error: {setRemoteOp.Error.message}");
                        yield break;
                    }

                    Debug.Log("Answer applied.");
                    yield break;
                }
            }

            yield return new WaitForSeconds(0.5f);
        }

        Debug.LogError("Timed out waiting for answer.");
    }

}