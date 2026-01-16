from aiohttp import web
import json

# deviceId -> {"publisher": ws, "viewer": ws}
PEERS = {}

async def ws_handler(request: web.Request):
    ws = web.WebSocketResponse(heartbeat=20)
    await ws.prepare(request)

    role = None
    device_id = None

    async def send(ws2, obj):
        if ws2 is not None and not ws2.closed:
            await ws2.send_str(json.dumps(obj))

    try:
        async for msg in ws:
            if msg.type != web.WSMsgType.TEXT:
                continue

            try:
                data = json.loads(msg.data)
            except Exception:
                await send(ws, {"type": "error", "message": "invalid json"})
                continue

            mtype = data.get("type")

            # First message must be "join"
            if mtype == "join":
                role = data.get("role")      # "publisher" or "viewer"
                device_id = data.get("deviceId")

                if role not in ("publisher", "viewer") or not device_id:
                    await send(ws, {"type": "error", "message": "join requires role and deviceId"})
                    continue

                entry = PEERS.setdefault(device_id, {"publisher": None, "viewer": None})
                entry[role] = ws

                await send(ws, {"type": "joined", "role": role, "deviceId": device_id})

                other_role = "viewer" if role == "publisher" else "publisher"
                await send(entry.get(other_role), {
                    "type": "peer-status",
                    "deviceId": device_id,
                    "peer": role,
                    "status": "online"
                })
                continue

            if device_id is None or role is None:
                await send(ws, {"type": "error", "message": "must join first"})
                continue

            # Relay signaling messages to the other peer in the same deviceId room
            # Added "ready" so viewer can request publisher to (re)send an offer.
            if mtype in ("offer", "answer", "candidate", "ready"):
                entry = PEERS.get(device_id, {})
                target = entry.get("viewer" if role == "publisher" else "publisher")
                await send(target, data)
                continue

            await send(ws, {"type": "error", "message": f"unknown message type: {mtype}"})

    finally:
        # Cleanup on disconnect
        if device_id and role:
            entry = PEERS.get(device_id)
            if entry and entry.get(role) is ws:
                entry[role] = None

                other_role = "viewer" if role == "publisher" else "publisher"
                other_ws = entry.get(other_role)

                # Notify the other side that this peer went offline
                if other_ws is not None and not other_ws.closed:
                    await other_ws.send_str(json.dumps({
                        "type": "peer-status",
                        "deviceId": device_id,
                        "peer": role,
                        "status": "offline"
                    }))

            # Remove empty rooms
            entry = PEERS.get(device_id)
            if entry and entry.get("publisher") is None and entry.get("viewer") is None:
                PEERS.pop(device_id, None)

    return ws


INDEX_HTML = r"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Quest WebRTC Viewer (WS)</title>
  <style>
    body { font-family: sans-serif; margin: 24px; }
    video { width: 960px; max-width: 100%; background: #000; }
    input, button { font-size: 16px; padding: 6px 10px; }
    .row { margin: 12px 0; }
    pre { white-space: pre-wrap; }
  </style>
</head>
<body>
  <h2>Quest WebRTC Viewer (WebSockets)</h2>

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

function log(s){ logEl.textContent += s + "\n"; }

connectBtn.onclick = async () => {
  const deviceId = deviceIdEl.value.trim();
  if (!deviceId) return;
  logEl.textContent = "";

  const wsUrl = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws";
  const ws = new WebSocket(wsUrl);

  let pc = null;

  ws.onopen = () => {
    log("WS open. Joining as viewer...");
    ws.send(JSON.stringify({ type: "join", role: "viewer", deviceId }));
  };

  ws.onmessage = async (ev) => {
    const msg = JSON.parse(ev.data);

    if (msg.type === "joined") {
      log(`Joined room deviceId=${msg.deviceId} role=${msg.role}`);
      // NEW: tell publisher we're ready so it can (re)send an offer
      ws.send(JSON.stringify({ type: "ready" }));
      log("Sent ready to publisher.");
      return;
    }

    if (msg.type === "peer-status") {
      log(`Peer ${msg.peer} is ${msg.status}`);
      return;
    }

    if (msg.type === "offer") {
      log("Received offer. Creating RTCPeerConnection...");

      pc = new RTCPeerConnection({
        iceServers: [{ urls: ["stun:stun.l.google.com:19302"] }]
      });

      // NEW: explicitly request receiving video
      pc.addTransceiver("video", { direction: "recvonly" });

      pc.ontrack = (e) => {
        log("Received track.");
        videoEl.srcObject = e.streams[0];
      };

      pc.onicecandidate = (e) => {
        if (e.candidate) {
          ws.send(JSON.stringify({
            type: "candidate",
            candidate: e.candidate.candidate,
            sdpMid: e.candidate.sdpMid,
            sdpMLineIndex: e.candidate.sdpMLineIndex
          }));
        }
      };

      pc.onconnectionstatechange = () => log("PC state: " + pc.connectionState);
      pc.oniceconnectionstatechange = () => log("ICE state: " + pc.iceConnectionState);

      await pc.setRemoteDescription({ type: "offer", sdp: msg.sdp });
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);

      ws.send(JSON.stringify({ type: "answer", sdp: pc.localDescription.sdp }));
      log("Answer sent (trickle ICE running).");
      return;
    }

    if (msg.type === "candidate" && pc) {
      try {
        await pc.addIceCandidate({
          candidate: msg.candidate,
          sdpMid: msg.sdpMid,
          sdpMLineIndex: msg.sdpMLineIndex
        });
      } catch (e) {
        log("addIceCandidate error: " + e);
      }
      return;
    }

    if (msg.type === "error") {
      log("Server error: " + msg.message);
      return;
    }

    log("Unhandled message: " + JSON.stringify(msg));
  };

  ws.onclose = () => log("WS closed.");
  ws.onerror = () => log("WS error.");
};
</script>
</body>
</html>
"""

async def index(request: web.Request):
    return web.Response(text=INDEX_HTML, content_type="text/html")

app = web.Application()
app.router.add_get("/", index)
app.router.add_get("/ws", ws_handler)

if __name__ == "__main__":
    web.run_app(app, host="0.0.0.0", port=8080)