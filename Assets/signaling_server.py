from aiohttp import web
import json

# In-memory store (fine for local dev; later you can back this with Redis)
OFFERS = {}   # deviceId -> {"type":"offer","sdp":"..."}
ANSWERS = {}  # deviceId -> {"type":"answer","sdp":"..."}

async def post_offer(request: web.Request):
    device_id = request.match_info["deviceId"]
    data = await request.json()
    OFFERS[device_id] = data
    # clear stale answer when a new offer arrives
    ANSWERS.pop(device_id, None)
    return web.json_response({"ok": True})

async def get_offer(request: web.Request):
    device_id = request.match_info["deviceId"]
    offer = OFFERS.get(device_id)
    if not offer:
        return web.Response(status=404, text="no offer")
    return web.json_response(offer)

async def post_answer(request: web.Request):
    device_id = request.match_info["deviceId"]
    data = await request.json()
    ANSWERS[device_id] = data
    return web.json_response({"ok": True})

async def get_answer(request: web.Request):
    device_id = request.match_info["deviceId"]
    ans = ANSWERS.get(device_id)
    if not ans:
        return web.Response(status=404, text="no answer")
    return web.json_response(ans)

async def post_offer(request):
    device_id = request.match_info["deviceId"]
    data = await request.json()
    print("POST /offer/", device_id, "len=", len(data.get("sdp","")))
    OFFERS[device_id] = data
    ANSWERS.pop(device_id, None)
    return web.json_response({"ok": True})


INDEX_HTML = r"""
<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <title>Quest WebRTC Viewer</title>
    <style>
      body { font-family: sans-serif; margin: 24px; }
      video { width: 960px; max-width: 100%; background: #000; }
      input, button { font-size: 16px; padding: 6px 10px; }
      .row { margin: 12px 0; }
      pre { white-space: pre-wrap; }
    </style>
  </head>
  <body>
    <h2>Quest WebRTC Viewer</h2>

    <div class="row">
      Device ID:
      <input id="deviceId" value="quest3" />
      <button id="connectBtn">Connect</button>
    </div>

    <video id="video" autoplay playsinline></video>
    <div class="row"><pre id="log"></pre></div>

    <script>
      const logEl = document.getElementById("log");
      const videoEl = document.getElementById("video");
      const connectBtn = document.getElementById("connectBtn");
      const deviceIdEl = document.getElementById("deviceId");

      function log(msg) { logEl.textContent += msg + "\n"; }

      async function waitIceComplete(pc, timeoutMs=5000) {
        if (pc.iceGatheringState === "complete") return;
        await new Promise((resolve) => {
          const t = setTimeout(() => { pc.removeEventListener("icegatheringstatechange", onChange); resolve(); }, timeoutMs);
          function onChange() {
            if (pc.iceGatheringState === "complete") {
              clearTimeout(t);
              pc.removeEventListener("icegatheringstatechange", onChange);
              resolve();
            }
          }
          pc.addEventListener("icegatheringstatechange", onChange);
        });
      }

      connectBtn.onclick = async () => {
        const deviceId = deviceIdEl.value.trim();
        if (!deviceId) return;

        logEl.textContent = "";
        log("Fetching offer for " + deviceId + "...");

        const offerRes = await fetch(`/offer/${deviceId}`);
        if (!offerRes.ok) { log("No offer yet (is Quest running?)"); return; }
        const offer = await offerRes.json();

        // For LAN, STUN is optional, but keep it for future portability.
        const pc = new RTCPeerConnection({
          iceServers: [{ urls: ["stun:stun.l.google.com:19302"] }]
        });

        pc.ontrack = (e) => {
          log("Received track.");
          videoEl.srcObject = e.streams[0];
        };

        pc.onconnectionstatechange = () => log("PC state: " + pc.connectionState);
        pc.oniceconnectionstatechange = () => log("ICE state: " + pc.iceConnectionState);

        await pc.setRemoteDescription(offer);
        log("Offer set. Creating answer...");

        pc.addTransceiver("video", { direction: "recvonly" });
        
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await waitIceComplete(pc, 5000);

        // Use final SDP with candidates
        const finalAnswer = pc.localDescription;

        log("Posting answer...");
        const ansRes = await fetch(`/answer/${deviceId}`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ type: "answer", sdp: finalAnswer.sdp })
        });

        if (!ansRes.ok) { log("Failed to post answer."); return; }
        log("Answer posted. If Quest polls successfully, video should appear.");
      };
    </script>
  </body>
</html>
"""

async def index(request: web.Request):
    return web.Response(text=INDEX_HTML, content_type="text/html")

app = web.Application()
app.router.add_get("/", index)
app.router.add_post("/offer/{deviceId}", post_offer)
app.router.add_get("/offer/{deviceId}", get_offer)
app.router.add_post("/answer/{deviceId}", post_answer)
app.router.add_get("/answer/{deviceId}", get_answer)

if __name__ == "__main__":
    web.run_app(app, host="0.0.0.0", port=8080)