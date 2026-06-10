"""
SAFER AR / Quest Video Stream — WebSocket Relay Server
------------------------------------------------------
Architecture:
  Quest (publisher) --JPEG--> this server --JPEG--> Browser (viewer)

Usage:
  pip install aiohttp
  python server.py

Then open http://localhost:8080 in your browser.
On Quest set signalingWsUrl = "ws://<your-ip>:8080/ws"
"""

import asyncio
import json
import logging
from aiohttp import web

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("ws-server")

# PEERS[deviceId] = {"publisher": ws, "viewer": ws}
PEERS: dict[str, dict] = {}


# ─── WebSocket handler ────────────────────────────────────────────────────────

async def ws_handler(request: web.Request) -> web.WebSocketResponse:
    ws = web.WebSocketResponse()
    await ws.prepare(request)

    device_id = None
    role = None

    log.info("New WS connection from %s", request.remote)

    async for msg in ws:
        if msg.type == web.WSMsgType.TEXT:
            try:
                data = json.loads(msg.data)
            except json.JSONDecodeError:
                continue

            msg_type = data.get("type")

            # ── join ──────────────────────────────────────────────────────────
            if msg_type == "join":
                role = data.get("role")          # "publisher" or "viewer"
                device_id = data.get("deviceId", "default")

                if device_id not in PEERS:
                    PEERS[device_id] = {}

                PEERS[device_id][role] = ws
                log.info("[%s] %s joined", device_id, role)

                # Notify the other peer if already connected
                other_role = "viewer" if role == "publisher" else "publisher"
                other_ws = PEERS[device_id].get(other_role)
                if other_ws and not other_ws.closed:
                    await other_ws.send_str(json.dumps({
                        "type": "peer-status",
                        "status": "connected",
                        "role": role
                    }))

            # ── ready ─────────────────────────────────────────────────────────
            elif msg_type == "ready":
                log.info("[%s] %s is ready", device_id, role)
                # Relay to other peer
                if device_id and role:
                    other_role = "viewer" if role == "publisher" else "publisher"
                    other_ws = PEERS.get(device_id, {}).get(other_role)
                    if other_ws and not other_ws.closed:
                        await other_ws.send_str(json.dumps({"type": "ready"}))

            # ── ping ──────────────────────────────────────────────────────────
            elif msg_type == "ping":
                await ws.send_str(json.dumps({"type": "pong"}))

            else:
                # Relay any other text messages to the other peer
                if device_id and role:
                    other_role = "viewer" if role == "publisher" else "publisher"
                    other_ws = PEERS.get(device_id, {}).get(other_role)
                    if other_ws and not other_ws.closed:
                        await other_ws.send_str(msg.data)

        elif msg.type == web.WSMsgType.BINARY:
            # JPEG frame from publisher → forward to viewer
            if device_id and role == "publisher":
                viewer_ws = PEERS.get(device_id, {}).get("viewer")
                if viewer_ws and not viewer_ws.closed:
                    await viewer_ws.send_bytes(msg.data)

        elif msg.type in (web.WSMsgType.ERROR, web.WSMsgType.CLOSE):
            break

    # Cleanup on disconnect
    if device_id and role and device_id in PEERS:
        PEERS[device_id].pop(role, None)
        log.info("[%s] %s disconnected", device_id, role)

        # Notify other peer
        other_role = "viewer" if role == "publisher" else "publisher"
        other_ws = PEERS.get(device_id, {}).get(other_role)
        if other_ws and not other_ws.closed:
            await other_ws.send_str(json.dumps({
                "type": "peer-status",
                "status": "disconnected",
                "role": role
            }))

    return ws


# ─── Browser viewer page ──────────────────────────────────────────────────────

async def index_handler(request: web.Request) -> web.Response:
    device_id = request.query.get("deviceId", "quest3")
    html = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Quest Stream — {device_id}</title>
<style>
  * {{ box-sizing: border-box; margin: 0; padding: 0 }}
  body {{ background: #0f1117; color: #e0e0e0;
         font-family: -apple-system, sans-serif;
         display: flex; flex-direction: column;
         align-items: center; min-height: 100vh; padding: 20px }}
  h1 {{ font-size: 1.2rem; margin-bottom: 12px; color: #fff }}
  #status {{ font-size: .85rem; color: #aaa; margin-bottom: 12px }}
  #status.ok {{ color: #68d391 }}
  #status.err {{ color: #fc8181 }}
  canvas {{ max-width: 100%; border-radius: 8px;
            border: 1px solid #2d2d3a; background: #000 }}
  #stats {{ font-size: .75rem; color: #555; margin-top: 8px; font-family: monospace }}
</style>
</head>
<body>
<h1>🎥 Quest Live Stream</h1>
<div id="status">Connecting...</div>
<canvas id="c"></canvas>
<div id="stats">fps: — &nbsp; resolution: —</div>

<script>
const DEVICE_ID = "{device_id}";
const WS_URL = "ws://" + location.host + "/ws";

const canvas = document.getElementById("c");
const ctx = canvas.getContext("2d");
const statusEl = document.getElementById("status");
const statsEl = document.getElementById("stats");

let ws, frameCount = 0, lastTime = performance.now();

function connect() {{
  ws = new WebSocket(WS_URL);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => {{
    statusEl.textContent = "Connected — waiting for Quest...";
    statusEl.className = "";
    ws.send(JSON.stringify({{ type: "join", role: "viewer", deviceId: DEVICE_ID }}));
    ws.send(JSON.stringify({{ type: "ready" }}));
  }};

  ws.onmessage = (e) => {{
    if (e.data instanceof ArrayBuffer) {{
      // JPEG frame
      const blob = new Blob([e.data], {{ type: "image/jpeg" }});
      const url = URL.createObjectURL(blob);
      const img = new Image();
      img.onload = () => {{
        canvas.width = img.width;
        canvas.height = img.height;
        ctx.drawImage(img, 0, 0);
        URL.revokeObjectURL(url);

        frameCount++;
        const now = performance.now();
        if (now - lastTime >= 1000) {{
          const fps = (frameCount / ((now - lastTime) / 1000)).toFixed(1);
          statsEl.textContent = `fps: ${{fps}}  resolution: ${{img.width}}x${{img.height}}`;
          frameCount = 0;
          lastTime = now;
        }}

        statusEl.textContent = "Streaming ✅";
        statusEl.className = "ok";
      }};
      img.src = url;
    }} else {{
      try {{
        const msg = JSON.parse(e.data);
        if (msg.type === "peer-status") {{
          if (msg.status === "connected") {{
            statusEl.textContent = "Quest connected — starting stream...";
          }} else {{
            statusEl.textContent = "Quest disconnected";
            statusEl.className = "err";
          }}
        }}
      }} catch(err) {{}}
    }}
  }};

  ws.onclose = () => {{
    statusEl.textContent = "Disconnected — reconnecting...";
    statusEl.className = "err";
    setTimeout(connect, 2000);
  }};

  ws.onerror = () => {{
    statusEl.textContent = "Connection error";
    statusEl.className = "err";
  }};
}}

connect();
</script>
</body>
</html>"""
    return web.Response(text=html, content_type="text/html")


# ─── App setup ────────────────────────────────────────────────────────────────

app = web.Application()
app.router.add_get("/", index_handler)
app.router.add_get("/ws", ws_handler)

if __name__ == "__main__":
    log.info("Starting server on http://0.0.0.0:8080")
    log.info("Open http://localhost:8080?deviceId=quest3 in your browser")
    web.run_app(app, host="0.0.0.0", port=8080)
