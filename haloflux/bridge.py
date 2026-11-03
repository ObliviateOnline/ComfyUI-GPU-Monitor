#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HALOFLUX x ComfyUI GPU Monitor - data bridge
============================================
Polls the machine (NVIDIA GPU / CPU / RAM / ComfyUI) once a second and exposes:

    GET /api/stats    -> live JSON (also carries a rolling history window)
    GET /api/health   -> {"ok": true, "uptime": ...}
    GET /*            -> static files from ./site

Everything else (the visual side) lives in site/lab.html, which fetches
/api/stats once a second and repaints the ring + header KPIs.

No third-party dependencies: nvidia-smi for the GPU, ctypes for CPU/RAM.
Requires Windows (CPU/RAM come from kernel32); on other platforms it degrades
gracefully and only reports the GPU part.

Usage:
    python bridge.py                 # serve on 8850 (auto-picks a free port)
    python bridge.py --port 9000
    python bridge.py --interval 0.5  # faster sampling
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.request
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

SITE = Path(__file__).resolve().parent / "site"
COMFY_URL = os.environ.get("COMFYUI_URL", "http://127.0.0.1:8188").rstrip("/")
HIST_LEN = 180                     # 3 minutes at 1 Hz
CREATE_NO_WINDOW = 0x08000000      # don't flash a console window for nvidia-smi

MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".mp3": "audio/mpeg",
    ".svg": "image/svg+xml",
    ".ico": "image/x-icon",
}

# --------------------------------------------------------------------------
# Windows system counters (ctypes, no psutil)
# --------------------------------------------------------------------------


class _FILETIME(ctypes.Structure):
    _fields_ = [("lo", wt.DWORD), ("hi", wt.DWORD)]


def _ft(v: "_FILETIME") -> int:
    return (v.hi << 32) | v.lo


class _MEMSTAT(ctypes.Structure):
    _fields_ = [
        ("dwLength", wt.DWORD),
        ("dwMemoryLoad", wt.DWORD),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


class SysCounters:
    """CPU utilisation + physical memory, via kernel32 only."""

    def __init__(self) -> None:
        self.ok = sys.platform == "win32"
        self._last = None
        self.cpu = 0.0
        self.logical = os.cpu_count() or 0
        self.cpu_name = ""
        if self.ok:
            try:
                self._k32 = ctypes.WinDLL("kernel32", use_last_error=True)
            except OSError:
                self.ok = False
        if self.ok:
            self._prime()
            self.cpu_name = self._read_cpu_name()

    # -- cpu ---------------------------------------------------------------
    def _prime(self) -> None:
        i, k, u = _FILETIME(), _FILETIME(), _FILETIME()
        if self._k32.GetSystemTimes(ctypes.byref(i), ctypes.byref(k), ctypes.byref(u)):
            self._last = (_ft(i), _ft(k), _ft(u))

    def sample_cpu(self) -> float:
        if not self.ok:
            return self.cpu
        i, k, u = _FILETIME(), _FILETIME(), _FILETIME()
        if not self._k32.GetSystemTimes(ctypes.byref(i), ctypes.byref(k), ctypes.byref(u)):
            return self.cpu
        cur = (_ft(i), _ft(k), _ft(u))
        if self._last:
            d_idle = cur[0] - self._last[0]
            # kernel time already includes idle time
            d_total = (cur[1] - self._last[1]) + (cur[2] - self._last[2])
            if d_total > 0:
                busy = d_total - d_idle
                self.cpu = max(0.0, min(100.0, busy * 100.0 / d_total))
        self._last = cur
        return self.cpu

    # -- memory ------------------------------------------------------------
    def sample_mem(self) -> dict:
        if not self.ok:
            return {"used": 0, "total": 0, "pct": 0.0}
        m = _MEMSTAT()
        m.dwLength = ctypes.sizeof(_MEMSTAT)
        if not self._k32.GlobalMemoryStatusEx(ctypes.byref(m)):
            return {"used": 0, "total": 0, "pct": 0.0}
        total = m.ullTotalPhys
        used = total - m.ullAvailPhys
        return {
            "used": int(used // (1024 * 1024)),
            "total": int(total // (1024 * 1024)),
            "pct": round(m.dwMemoryLoad, 1),
        }

    def _read_cpu_name(self) -> str:
        try:
            import winreg

            key = winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            )
            name, _ = winreg.QueryValueEx(key, "ProcessorNameString")
            return " ".join(str(name).split())
        except Exception:
            return ""


# --------------------------------------------------------------------------
# GPU (nvidia-smi)
# --------------------------------------------------------------------------

SMI_FIELDS = (
    "name,utilization.gpu,memory.used,memory.total,temperature.gpu,"
    "power.draw,fan.speed,clocks.sm"
)


def _num(txt: str, default: float = 0.0) -> float:
    t = txt.strip()
    if not t or t.startswith("[") or t.lower() in ("n/a", "unknown"):
        return default
    try:
        return float(t)
    except ValueError:
        return default


def read_gpu() -> dict:
    """One nvidia-smi call; ~60-120 ms. Returns ok=False when it fails."""
    try:
        proc = subprocess.run(
            ["nvidia-smi", f"--query-gpu={SMI_FIELDS}", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=4,
            creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        return {"ok": False, "error": f"nvidia-smi: {exc}"}
    if proc.returncode != 0:
        return {"ok": False, "error": (proc.stderr or "").strip()[:200] or "nvidia-smi failed"}

    line = (proc.stdout or "").strip().splitlines()
    if not line:
        return {"ok": False, "error": "nvidia-smi: empty output"}
    parts = [p.strip() for p in line[0].split(",")]
    if len(parts) < 8:
        return {"ok": False, "error": "nvidia-smi: unexpected output"}

    gpu = {
        "ok": True,
        "name": parts[0],
        "util": _num(parts[1]),
        "mem_used": _num(parts[2]),
        "mem_total": _num(parts[3]),
        "temp": _num(parts[4]),
        "power": _num(parts[5]),
        "fan": _num(parts[6]),
        "clock": _num(parts[7]),
    }
    total = gpu["mem_total"] or 1
    gpu["mem_pct"] = round(gpu["mem_used"] * 100.0 / total, 1)
    return gpu


# --------------------------------------------------------------------------
# ComfyUI
# --------------------------------------------------------------------------


def _get_json(url: str, timeout: float = 1.5):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def read_compute_apps() -> list:
    """Processes holding a GPU context.

    WDDM does not expose per-process VRAM through nvidia-smi (it answers
    [N/A]), so this reports names + pids only - no invented memory numbers.
    """
    try:
        proc = subprocess.run(
            ["nvidia-smi", "--query-compute-apps=pid,process_name", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=4,
            creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError):
        return []
    if proc.returncode != 0:
        return []

    rows, seen = [], set()
    for line in (proc.stdout or "").splitlines():
        parts = [p.strip() for p in line.split(",", 1)]
        if len(parts) < 2:
            continue
        pid, name = parts[0], parts[1]
        if not pid.isdigit() or "Insufficient Permissions" in name:
            continue
        base = os.path.basename(name.strip()) or name.strip()
        if base.lower() in seen:
            continue
        seen.add(base.lower())
        rows.append({"pid": pid, "name": base})
        if len(rows) >= 8:
            break
    return rows


def poll_comfy_jobs(limit: int = 8) -> list:
    """Recent prompts from /history, summarised."""
    try:
        h = _get_json(COMFY_URL + "/history?max_items=16", 2.0)
    except Exception:
        return []
    jobs = []
    for pid, entry in (h or {}).items():
        if not isinstance(entry, dict):
            continue
        st = entry.get("status") or {}
        status = st.get("status_str") or "unknown"
        start = done = None
        for msg in (st.get("messages") or []):
            if not isinstance(msg, (list, tuple)) or len(msg) < 2:
                continue
            kind, data = msg[0], (msg[1] or {})
            if kind == "execution_start":
                start = data.get("timestamp")
            elif kind in ("execution_success", "execution_error", "execution_interrupted"):
                done = data.get("timestamp")
        nodes = 0
        prompt = entry.get("prompt") or []
        if len(prompt) > 2 and isinstance(prompt[2], dict):
            nodes = len(prompt[2])
        outputs = 0
        for v in (entry.get("outputs") or {}).values():
            if isinstance(v, dict):
                outputs += len(v.get("images") or [])
        dur = round((done - start) / 1000.0, 2) if (start and done and done > start) else None
        ts = done or start
        jobs.append({
            "id": str(pid)[:8],
            "status": "ok" if status == "success" else status,
            "nodes": nodes,
            "outputs": outputs,
            "dur": dur,
            "at": time.strftime("%H:%M:%S", time.localtime(ts / 1000.0)) if ts else "",
        })
    jobs.sort(key=lambda j: j["at"], reverse=True)
    return jobs[:limit]


def poll_comfy() -> dict:
    out = {
        "online": False,
        "running": 0,
        "pending": 0,
        "executing": "",
        "running_ids": [],
        "pending_ids": [],
        "vram_total": 0,
        "vram_free": 0,
        "error": "",
    }
    try:
        q = _get_json(COMFY_URL + "/queue", 1.5)
        out["online"] = True
        run = q.get("queue_running") or []
        pend = q.get("queue_pending") or []
        out["running"] = len(run)
        out["pending"] = len(pend)

        def _id(item):
            return str(item[1])[:8] if isinstance(item, (list, tuple)) and len(item) > 1 else ""

        out["running_ids"] = [_id(x) for x in run[:8] if _id(x)]
        out["pending_ids"] = [_id(x) for x in pend[:8] if _id(x)]
        if out["running_ids"]:
            out["executing"] = out["running_ids"][0]
    except Exception as exc:
        out["error"] = str(exc)[:120]
        return out
    try:
        st = _get_json(COMFY_URL + "/system_stats", 1.5)
        devs = st.get("devices") or []
        if devs:
            out["vram_total"] = int(devs[0].get("vram_total") or 0)
            out["vram_free"] = int(devs[0].get("vram_free") or 0)
    except Exception:
        pass
    return out


# --------------------------------------------------------------------------
# Shared state
# --------------------------------------------------------------------------


class State:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.gpu = {"ok": False, "error": "warming up"}
        self.cpu = {"util": 0.0, "logical": 0, "name": ""}
        self.ram = {"used": 0, "total": 0, "pct": 0.0}
        self.comfy = {"online": False, "running": 0, "pending": 0}
        self.compute = []
        self.jobs = []
        self.events = deque(maxlen=40)
        self.hist = {
            "gpu": deque(maxlen=HIST_LEN),
            "vram": deque(maxlen=HIST_LEN),
            "cpu": deque(maxlen=HIST_LEN),
            "temp": deque(maxlen=HIST_LEN),
        }
        self.started = time.time()
        self.ticks = 0

    def push_hist(self) -> None:
        with self.lock:
            self.hist["gpu"].append(round(float(self.gpu.get("util") or 0), 1))
            self.hist["vram"].append(round(float(self.gpu.get("mem_used") or 0), 1))
            self.hist["cpu"].append(round(float(self.cpu.get("util") or 0), 1))
            self.hist["temp"].append(round(float(self.gpu.get("temp") or 0), 1))

    def snapshot(self) -> dict:
        with self.lock:
            gpu = dict(self.gpu)
            cpu = dict(self.cpu)
            ram = dict(self.ram)
            comfy = dict(self.comfy)
            hist = {k: list(v) for k, v in self.hist.items()}
            return {
                "ts": time.time(),
                "uptime": round(time.time() - self.started, 1),
                "ticks": self.ticks,
                "gpu": gpu,
                "cpu": cpu,
                "ram": ram,
                "comfy": comfy,
                "compute": list(self.compute),
                "jobs": list(self.jobs),
                "events": list(self.events),
                "comfy_url": COMFY_URL,
                "hist": hist,
            }


class Sampler(threading.Thread):
    def __init__(self, state: State, interval: float) -> None:
        super().__init__(daemon=True, name="sampler")
        self.state = state
        self.interval = max(0.2, interval)
        self.sys = SysCounters()
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        with self.state.lock:
            self.state.cpu["logical"] = self.sys.logical
            self.state.cpu["name"] = self.sys.cpu_name
        while not self._stop.is_set():
            gpu = read_gpu()
            cpu_util = self.sys.sample_cpu()
            ram = self.sys.sample_mem()
            apps = read_compute_apps() if self.state.ticks % 3 == 0 else None
            with self.state.lock:
                self.state.gpu = gpu
                self.state.cpu["util"] = round(cpu_util, 1)
                self.state.ram = ram
                if apps is not None:
                    self.state.compute = apps
                self.state.ticks += 1
            self.state.push_hist()
            self._stop.wait(self.interval)


class ComfyPoller(threading.Thread):
    def __init__(self, state: State, interval: float = 2.0) -> None:
        super().__init__(daemon=True, name="comfy")
        self.state = state
        self.interval = interval
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        prev = None
        seen_jobs = set()
        first = True
        while not self._stop.is_set():
            comfy = poll_comfy()
            jobs = poll_comfy_jobs()
            now = time.strftime("%H:%M:%S")
            fresh = []

            if first:
                # don't replay the whole backlog into the feed on startup
                seen_jobs = {j["id"] for j in jobs}
                first = False
            else:
                if prev is not None and prev.get("online") != comfy.get("online"):
                    fresh.append(("up" if comfy.get("online") else "down",
                                  "ComfyUI 已连接 · 端口 " + COMFY_URL.rsplit(":", 1)[-1]
                                  if comfy.get("online") else "ComfyUI 已断开"))
                if prev is not None and comfy.get("running", 0) > prev.get("running", 0):
                    if comfy.get("executing"):
                        fresh.append(("run", "开始执行 " + comfy["executing"]))
                for j in jobs:
                    if j["id"] in seen_jobs:
                        continue
                    seen_jobs.add(j["id"])
                    if j["status"] == "ok":
                        fresh.append(("ok", "完成 %s · %d 节点 · %s · %d 输出" % (
                            j["id"], j["nodes"],
                            ("%.1fs" % j["dur"]) if j["dur"] else "时长未知",
                            j["outputs"])))
                    elif j["status"] not in ("unknown", ""):
                        fresh.append(("err", "%s %s" % (j["id"], j["status"])))
                if len(seen_jobs) > 200:
                    seen_jobs = {j["id"] for j in jobs}

            with self.state.lock:
                self.state.comfy = comfy
                self.state.jobs = jobs
                for kind, text in fresh:
                    self.state.events.appendleft({"t": now, "kind": kind, "text": text})

            prev = comfy
            self._stop.wait(self.interval)


# --------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------


def make_handler(state: State):
    class Handler(BaseHTTPRequestHandler):
        server_version = "HALOFLUXBridge/1.0"
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):  # keep the console quiet
            pass

        def _send(self, code: int, body: bytes, ctype: str, cache: str = "no-store") -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", cache)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def _json(self, obj, code: int = 200) -> None:
            self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"),
                       "application/json; charset=utf-8")

        def do_GET(self):
            path = self.path.split("?", 1)[0]
            if path == "/api/stats":
                self._json(state.snapshot())
                return
            if path == "/api/health":
                self._json({"ok": True, "uptime": round(time.time() - state.started, 1),
                            "ticks": state.ticks})
                return
            if path in ("/", ""):
                path = "/index.html"
            rel = path.lstrip("/").replace("\\", "/")
            target = (SITE / rel).resolve()
            try:
                target.relative_to(SITE)
            except ValueError:
                self._send(403, b"forbidden", "text/plain; charset=utf-8")
                return
            if target.is_dir():
                target = target / "index.html"
            if not target.is_file():
                self._send(404, b"not found", "text/plain; charset=utf-8")
                return
            ctype = MIME.get(target.suffix.lower(), "application/octet-stream")
            cache = "no-store" if target.suffix.lower() in (".html", ".js") else "max-age=3600"
            try:
                body = target.read_bytes()
            except OSError as exc:
                self._send(500, str(exc).encode(), "text/plain; charset=utf-8")
                return
            self._send(200, body, ctype, cache)

    return Handler


def pick_port(preferred: int) -> int:
    for port in range(preferred, preferred + 20):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind(("127.0.0.1", port))
                return port
            except OSError:
                continue
    raise SystemExit(f"no free port in {preferred}..{preferred + 20}")


def main() -> int:
    global COMFY_URL
    ap = argparse.ArgumentParser(description="HALOFLUX x ComfyUI GPU Monitor bridge")
    ap.add_argument("--port", type=int, default=8850)
    ap.add_argument("--interval", type=float, default=1.0, help="sampling period, seconds")
    ap.add_argument("--comfy", default=COMFY_URL, help="ComfyUI base url")
    ap.add_argument("--open", action="store_true", help="open the page in the default browser")
    args = ap.parse_args()

    COMFY_URL = args.comfy.rstrip("/")

    if not (SITE / "lab.html").is_file():
        print(f"[!] {SITE}\\lab.html not found - is the site folder intact?", flush=True)
        return 1

    state = State()
    sampler = Sampler(state, args.interval)
    comfy = ComfyPoller(state)
    sampler.start()
    comfy.start()

    port = pick_port(args.port)
    url = f"http://127.0.0.1:{port}/lab.html"
    httpd = ThreadingHTTPServer(("127.0.0.1", port), make_handler(state))
    httpd.daemon_threads = True

    print("=" * 58, flush=True)
    print("  HALOFLUX x ComfyUI GPU Monitor", flush=True)
    print(f"  {url}", flush=True)
    print(f"  ComfyUI: {COMFY_URL}", flush=True)
    print("  Ctrl+C to stop", flush=True)
    print("=" * 58, flush=True)

    if args.open:
        import webbrowser

        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n[stop] bye", flush=True)
    finally:
        sampler.stop()
        comfy.stop()
        httpd.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
