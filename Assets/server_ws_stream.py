from aiohttp import web
import json

# deviceId -> {"publisher": ws, "viewer": ws}
PEERS = {}

async def ws_handler(request: web.Request):
    ws = web.WebSocketResponse(heartbeat=20, max_msg_size=10 * 1024 * 1024)
    await ws.prepare(request)

    role = None
    device_id = None

    async def send_text(ws2, obj):
        if ws2 is not None and not ws2.closed:
            await ws2.send_str(json.dumps(obj))

    async def send_bin(ws2, data: bytes):
        if ws2 is not None and not ws2.closed:
            await ws2.send_bytes(data)

    try:
        async for msg in ws:
            if msg.type == web.WSMsgType.TEXT:
                try:
                    data = json.loads(msg.data)
                except Exception:
                    await send_text(ws, {"type": "error", "message": "invalid json"})
                    continue

                mtype = data.get("type")

                if mtype == "join":
                    role = data.get("role")  # "publisher" or "viewer"
                    device_id = data.get("deviceId")

                    if role not in ("publisher", "viewer") or not device_id:
                        await send_text(ws, {"type": "error", "message": "join requires role and deviceId"})
                        continue

                    entry = PEERS.setdefault(device_id, {"publisher": None, "viewer": None})
                    entry[role] = ws

                    await send_text(ws, {"type": "joined", "role": role, "deviceId": device_id})

                    other_role = "viewer" if role == "publisher" else "publisher"
                    await send_text(entry.get(other_role), {
                        "type": "peer-status",
                        "deviceId": device_id,
                        "peer": role,
                        "status": "online"
                    })
                    continue

                if device_id is None or role is None:
                    await send_text(ws, {"type": "error", "message": "must join first"})
                    continue

                # Optional control messages (start/stop, stats, etc.)
                if mtype in ("ready", "start", "stop", "ping"):
                    entry = PEERS.get(device_id, {})
                    target = entry.get("viewer" if role == "publisher" else "publisher")
                    await send_text(target, data)
                    continue

                await send_text(ws, {"type": "error", "message": f"unknown message type: {mtype}"})

            elif msg.type == web.WSMsgType.BINARY:
                # Forward binary frames publisher -> viewer only
                if device_id is None or role is None:
                    continue
                if role != "publisher":
                    continue

                entry = PEERS.get(device_id, {})
                viewer = entry.get("viewer")
                if viewer is not None and not viewer.closed:
                    await send_bin(viewer, msg.data)
                # else: drop frames silently

            elif msg.type == web.WSMsgType.ERROR:
                break

    finally:
        if device_id and role:
            entry = PEERS.get(device_id)
            if entry and entry.get(role) is ws:
                entry[role] = None

                other_role = "viewer" if role == "publisher" else "publisher"
                other_ws = entry.get(other_role)
                if other_ws is not None and not other_ws.closed:
                    await other_ws.send_str(json.dumps({
                        "type": "peer-status",
                        "deviceId": device_id,
                        "peer": role,
                        "status": "offline"
                    }))

            entry = PEERS.get(device_id)
            if entry and entry.get("publisher") is None and entry.get("viewer") is None:
                PEERS.pop(device_id, None)

    return ws


INDEX_HTML = r"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Quest Video Viewer (WebSocket Frames)</title>
  <style>
    body { font-family: sans-serif; margin: 24px; }
    img { width: 960px; max-width: 100%; background: #000; display:block; }
    input, button { font-size: 16px; padding: 6px 10px; }
    .row { margin: 12px 0; }
    pre { white-space: pre-wrap; }
  </style>
</head>
<body>
  <h2>Quest Video Viewer (WebSocket-only)</h2>

  <div class="row">
    Device ID:
    <input id="deviceId" value="quest3" />
    <button id="connectBtn">Connect</button>
  </div>

  <img id="frame" />
  <div class="row"><pre id="log"></pre></div>

<script>
const logEl = document.getElementById("log");
const frameEl = document.getElementById("frame");
const connectBtn = document.getElementById("connectBtn");
const deviceIdEl = document.getElementById("deviceId");

function log(s){ logEl.textContent += s + "\n"; }

let lastUrl = null;
let frames = 0;
let t0 = performance.now();

connectBtn.onclick = async () => {
  const deviceId = deviceIdEl.value.trim();
  if (!deviceId) return;
  logEl.textContent = "";
  frames = 0; t0 = performance.now();

  const wsUrl = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws";
  const ws = new WebSocket(wsUrl);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => {
    log("WS open. Joining as viewer...");
    ws.send(JSON.stringify({ type: "join", role: "viewer", deviceId }));
    ws.send(JSON.stringify({ type: "ready" }));
  };

  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") {
      const msg = JSON.parse(ev.data);
      if (msg.type === "joined") log(`Joined deviceId=${msg.deviceId} role=${msg.role}`);
      else if (msg.type === "peer-status") log(`Peer ${msg.peer} is ${msg.status}`);
      else log("MSG: " + ev.data);
      return;
    }

    // Binary frame
    const buf = ev.data; // ArrayBuffer
    const blob = new Blob([buf], { type: "image/jpeg" });
    const url = URL.createObjectURL(blob);

    frameEl.src = url;
    if (lastUrl) URL.revokeObjectURL(lastUrl);
    lastUrl = url;

    frames++;
    const dt = (performance.now() - t0) / 1000.0;
    if (dt >= 1.0) {
      log(`FPS ~ ${(frames/dt).toFixed(1)}  frame_bytes=${buf.byteLength}`);
      frames = 0; t0 = performance.now();
    }
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