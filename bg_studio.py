"""
bg_studio.py — engine of the "AI ფონი" tab. Works from one project file so the UI never has to juggle dozens of args.

    python bg_studio.py preview <project.json> <out.jpg> <view> <seconds>
        view = result | original | matte | highlight      (one processed frame)
    python bg_studio.py render  <project.json>
        → project["output"]; prints [PROGRESS] n, [ETA] seconds, [FRAME] <jpg> (a live look at the work), [SUCCESS]
    python bg_studio.py track   <project.json> <out.json>
        → follows every manual mark through the video (optical flow), keyframes keep it on target

Project JSON:
{
  "input": "...", "output": "...",
  "background": {"mode": "blur|color|image|video", "param": "..."},
  "invert": false,
  "quality": "fast|balanced|max",
  "refine": {"solidify": 60, "defringe": 60, "smooth": 30, "grow": 0, "feather": 0},
  "off": [[from_sec, to_sec], ...],                       # moments where the original stays
  "mode": "all" | "selected",                              # all = every person AI finds stays
  "people": {"analysis": "<face_blur analysis json>", "keep": [ids]},
  "marks": [{"id": 1, "type": "person|object", "name": "..", "keys": [{"f": 120, "box": [x, y, w, h]}], "track": "<tracks json>"}]
}
"""
import sys, os, json, time, subprocess, threading, queue
import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import virtual_bg as vb
import face_blur as fbm

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


def status(msg):
    print(f"[STATUS] {msg}", flush=True)


def load_project(path):
    with open(path, encoding="utf-8-sig") as fh:
        p = json.load(fh)
    p.setdefault("background", {"mode": "blur", "param": "35"})
    p.setdefault("refine", {})
    p["refine"] = {**{"solidify": 60, "defringe": 60, "smooth": 30, "grow": 0, "feather": 0}, **p["refine"]}
    p.setdefault("off", [])
    p.setdefault("mode", "all")
    p.setdefault("marks", [])
    return p


# ───────────────────────── manual marks: tracking ─────────────────────────
def _hold(boxes, f, reach=2):
    """box of a frame from a {frame: box} dict (exact, or a frame next to it); None = the mark is not there."""
    if not boxes:
        return None
    if f in boxes:
        return boxes[f]
    for d in range(1, reach + 1):
        if f - d in boxes:
            return boxes[f - d]
        if f + d in boxes:
            return boxes[f + d]
    return None


def is_cut(prev, cur):
    if prev is None or prev.shape != cur.shape:
        return False
    hc = cv2.compareHist(cv2.calcHist([prev], [0], None, [32], [0, 256]),
                         cv2.calcHist([cur], [0], None, [32], [0, 256]), cv2.HISTCMP_CORREL)
    return hc < 0.45 or (hc < 0.8 and cv2.absdiff(prev, cur).mean() > 40)


def track_one(cap, keys, total, fps, W, H, fs, back_limit_s=20.0):
    """Follow one mark through the video. keys = [(frame, box), ...] sorted. → ({frame: box}, [lost frames])"""
    keys = sorted(keys, key=lambda k: k[0])
    boxes, lost = {}, set()

    def gray_at(frame):
        g = cv2.cvtColor(cv2.resize(frame, None, fx=fs, fy=fs, interpolation=cv2.INTER_AREA) if fs < 1 else frame,
                         cv2.COLOR_BGR2GRAY)
        return g

    # ---- forward: key i → key i+1 (or the end). The error at the next key is spread over the segment. ----
    for i, (kf, kb) in enumerate(keys):
        end = keys[i + 1][0] if i + 1 < len(keys) else total - 1
        cap.set(cv2.CAP_PROP_POS_FRAMES, kf)
        ok, fr = cap.read()
        if not ok:
            boxes[kf] = tuple(kb)
            continue
        g = gray_at(fr)
        t = fbm.Track(1, kf, tuple(kb), 1.0)
        t.tmpl = fbm.patch_vec(g, t.box, fs)
        seg = {kf: tuple(kb)}
        prev = g
        for f in range(kf + 1, end + 1):
            ok, fr = cap.read()
            if not ok:
                break
            g = gray_at(fr)
            if is_cut(prev, g):                                  # a cut ends the shot: the mark does not exist after it
                break
            fbm.advance_tracks([t], prev, g, fs, W, H)
            if t.flow_bad > int(fps * 0.5):                    # lost: hold the last good position
                lost.add(f)
                t.flow_bad = 0
                t.pred = t.box
            t.box = t.pred
            if f % 8 == 0 and t.tmpl is not None:               # appearance follows slowly (turning, light)
                nv = fbm.patch_vec(g, t.box, fs)
                if nv is not None:
                    v = 0.85 * t.tmpl + 0.15 * nv
                    n = float(np.linalg.norm(v))
                    t.tmpl = v / n if n > 1e-3 else t.tmpl
            seg[f] = tuple(t.box)
            prev = g
        if i + 1 < len(keys) and end in seg:                     # land exactly on the next keyframe (same shot only)
            nb = np.array(keys[i + 1][1], np.float64)
            eb = np.array(seg[end], np.float64)
            err = nb - eb
            span = max(1, end - kf)
            for f, b in list(seg.items()):
                w = (f - kf) / span
                seg[f] = tuple((np.array(b, np.float64) + err * w).tolist())
        for f, b in seg.items():
            if f not in boxes or f == kf:
                boxes[f] = b

    # ---- backward from the first key (object was there before the user marked it) ----
    kf, kb = keys[0]
    start = max(0, kf - int(back_limit_s * fps))
    if kf > start:
        frames = []
        cap.set(cv2.CAP_PROP_POS_FRAMES, start)
        for f in range(start, kf + 1):
            ok, fr = cap.read()
            if not ok:
                break
            frames.append(gray_at(fr))
        if frames:
            t = fbm.Track(1, kf, tuple(kb), 1.0)
            t.tmpl = fbm.patch_vec(frames[-1], t.box, fs)
            prev = frames[-1]
            for f in range(kf - 1, start - 1, -1):
                idx = f - start
                if idx < 0 or idx >= len(frames):
                    break
                g = frames[idx]
                if is_cut(g, prev):
                    break
                fbm.advance_tracks([t], prev, g, fs, W, H)
                if t.flow_bad > int(fps * 0.5):
                    lost.add(f); t.flow_bad = 0; t.pred = t.box
                t.box = t.pred
                boxes[f] = tuple(t.box)
                prev = g
    return {int(k): [float(v) for v in b] for k, b in boxes.items()}, sorted(int(f) for f in lost)


def cmd_track(project_path, out_json):
    proj = load_project(project_path)
    cap = cv2.VideoCapture(proj["input"])
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fs = min(1.0, 640.0 / max(W, 1))
    res = {}
    marks = proj["marks"]
    for n, m in enumerate(marks):
        keys = [(int(k["f"]), k["box"]) for k in m.get("keys", [])]
        if not keys:
            continue
        status(f"ობიექტის მიყოლა: {m.get('name', m['id'])}…")
        boxes, lost = track_one(cap, keys, total, fps, W, H, fs)
        res[str(m["id"])] = {"boxes": {str(k): v for k, v in boxes.items()}, "lost": lost}
        print(f"[PROGRESS] {int((n + 1) / max(1, len(marks)) * 100)}", flush=True)
    cap.release()
    with open(out_json, "w", encoding="utf-8") as fh:
        json.dump({"fps": fps, "frames": total, "width": W, "height": H, "marks": res}, fh)
    print("[SUCCESS] track", flush=True)


# ───────────────────────── object cut-out (anything that is not a person) ─────────────────────────
class ObjectCutter:
    """GrabCut inside the (tracked) box; the previous result seeds the next frame, so it stays steady."""

    def __init__(self):
        self.prev = None          # previous mask (small, cropped), float 0..1
        self.prev_size = None

    def reset(self):
        self.prev = None

    def mask(self, frame, box):
        H, W = frame.shape[:2]
        x, y, w, h = box
        pad = 0.12
        x0, y0 = int(max(0, x - w * pad)), int(max(0, y - h * pad))
        x1, y1 = int(min(W, x + w * (1 + pad))), int(min(H, y + h * (1 + pad)))
        out = np.zeros((H, W), np.float32)
        if x1 - x0 < 8 or y1 - y0 < 8:
            return out
        crop = frame[y0:y1, x0:x1]
        sc = min(1.0, 224.0 / max(crop.shape[:2]))
        small = cv2.resize(crop, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA) if sc < 1 else crop.copy()
        sh, sw = small.shape[:2]
        gc = np.full((sh, sw), cv2.GC_BGD, np.uint8)
        ix0, iy0 = int((x - x0) * sc), int((y - y0) * sc)
        ix1, iy1 = int((x + w - x0) * sc), int((y + h - y0) * sc)
        ix0, iy0 = max(0, ix0), max(0, iy0)
        ix1, iy1 = min(sw, max(ix0 + 2, ix1)), min(sh, max(iy0 + 2, iy1))
        gc[iy0:iy1, ix0:ix1] = cv2.GC_PR_FGD
        mode = cv2.GC_INIT_WITH_RECT
        if self.prev is not None:
            pm = cv2.resize(self.prev, (sw, sh), interpolation=cv2.INTER_LINEAR)
            gc[:] = cv2.GC_BGD
            inner = np.zeros((sh, sw), bool); inner[iy0:iy1, ix0:ix1] = True
            gc[inner & (pm > 0.35)] = cv2.GC_PR_FGD
            gc[inner & (pm <= 0.35)] = cv2.GC_PR_BGD
            k = max(1, int(min(sh, sw) * 0.04))
            core = cv2.erode((pm > 0.7).astype(np.uint8), np.ones((2 * k + 1, 2 * k + 1), np.uint8))
            gc[(core > 0) & inner] = cv2.GC_FGD
            mode = cv2.GC_INIT_WITH_MASK
        bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
        try:
            if mode == cv2.GC_INIT_WITH_RECT:
                cv2.grabCut(small, gc, (ix0, iy0, max(2, ix1 - ix0), max(2, iy1 - iy0)), bgd, fgd, 3, cv2.GC_INIT_WITH_RECT)
            else:
                cv2.grabCut(small, gc, None, bgd, fgd, 2, cv2.GC_INIT_WITH_MASK)
        except cv2.error:
            return out
        m = ((gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD)).astype(np.float32)
        # keep the biggest blob only, then soften
        n, lab, stats, _ = cv2.connectedComponentsWithStats((m > 0).astype(np.uint8), connectivity=8)
        if n > 1:
            big = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
            m = (lab == big).astype(np.float32)
        self.prev = m
        m = cv2.GaussianBlur(m, (0, 0), max(1.0, min(sh, sw) * 0.012))
        m = cv2.resize(m, (x1 - x0, y1 - y0), interpolation=cv2.INTER_LINEAR)
        out[y0:y1, x0:x1] = m
        return out


def region_mask(shape, box, grow=0.08, feather=0.06):
    """Soft rounded rectangle around the box (the user drew it around the whole person)."""
    H, W = shape
    x, y, w, h = box
    gx, gy = w * grow, h * grow
    x0, y0, x1, y1 = int(x - gx), int(y - gy), int(x + w + gx), int(y + h + gy)
    sc = 0.25
    sm = np.zeros((max(2, int(H * sc)), max(2, int(W * sc))), np.float32)
    cv2.rectangle(sm, (int(x0 * sc), int(y0 * sc)), (int(x1 * sc), int(y1 * sc)), 1.0, -1)
    s = max(0.8, min(w, h) * feather * sc)
    sm = cv2.GaussianBlur(sm, (0, 0), s)
    return cv2.resize(sm, (W, H), interpolation=cv2.INTER_LINEAR)


# ───────────────────────── the per-frame pipeline ─────────────────────────
class Pipeline:
    def __init__(self, proj, W, H, fps, preview=False):
        self.proj, self.W, self.H, self.fps = proj, W, H, fps
        q = proj.get("quality", "balanced")
        qw, qi = vb.QUALITY.get(q, vb.QUALITY["balanced"])
        if preview and q != "fast":
            qw = 1280
        self.matte = vb.get_matte(qw, qi)
        self.refiner = vb.Refiner(proj["refine"])
        self.selector = None
        pe = proj.get("people")
        if proj["mode"] == "selected" and pe and pe.get("analysis") and os.path.isfile(pe["analysis"]):
            try:
                self.selector = vb.PersonSelector(pe["analysis"], pe.get("keep", []))
            except Exception as e:
                status(f"ადამიანების არჩევა ვერ ჩაიტვირთა: {e}")
        self.tracks = {}
        for m in proj["marks"]:
            tp = m.get("track")
            if tp and os.path.isfile(tp):
                try:
                    with open(tp, encoding="utf-8") as fh:
                        d = json.load(fh)
                    t = d["marks"].get(str(m["id"]))
                    if t:
                        self.tracks[m["id"]] = {int(k): v for k, v in t["boxes"].items()}
                except Exception:
                    pass
        self.cutters = {m["id"]: ObjectCutter() for m in proj["marks"] if m.get("type") == "object"}
        b = proj["background"]
        self.bg = vb.Background(b.get("mode", "blur"), str(b.get("param", "35")), W, H)
        self.off = [(float(a), float(b)) for a, b in proj.get("off", [])]
        self.invert = bool(proj.get("invert"))
        self.defr = float(proj["refine"].get("defringe", 0))

    def is_off(self, idx):
        t = idx / self.fps
        return any(a <= t <= b for a, b in self.off)

    def reset(self):
        self.matte.reset(); self.refiner.reset()
        for c in self.cutters.values():
            c.reset()

    def alpha(self, frame, idx):
        """final alpha (what stays) for this frame."""
        a = self.matte.alpha(frame)
        if self.matte.cut:
            self.refiner.reset()
        marks = self.proj["marks"]
        if self.proj["mode"] == "selected":
            keep = np.zeros_like(a)
            if self.selector is not None:
                keep = a * self.selector.mask(a, idx, self.W, self.H)
            for m in marks:
                if m.get("type") != "person":
                    continue
                b = _hold(self.tracks.get(m["id"]), idx)
                if b is not None:
                    keep = np.maximum(keep, a * region_mask(a.shape, b))
        else:
            keep = a
        for m in marks:                                      # non-person things the AI cannot see by itself
            if m.get("type") != "object":
                continue
            b = _hold(self.tracks.get(m["id"]), idx)
            if b is not None:
                keep = np.maximum(keep, self.cutters[m["id"]].mask(frame, b))
        return self.refiner.alpha(frame, keep)

    def compose(self, frame, a):
        return vb.compose(vb.defringe(frame, a, self.defr), a, self.bg.frame_for(frame), self.invert)

    def view(self, frame, a, view):
        if view == "original":
            return frame
        if view == "matte":
            m = (np.clip(a, 0, 1) * 255).astype(np.uint8)
            return cv2.cvtColor(m, cv2.COLOR_GRAY2BGR)
        if view == "highlight":                               # what will be replaced is tinted red, kept area has an outline
            out = frame.astype(np.float32)
            rep = 1.0 - np.clip(a, 0, 1)
            tint = np.zeros_like(out); tint[..., 2] = 255; tint[..., 1] = 40; tint[..., 0] = 40
            out = out * (1 - 0.55 * rep[..., None]) + tint * (0.55 * rep[..., None])
            edge = cv2.Canny((np.clip(a, 0, 1) * 255).astype(np.uint8), 60, 160)
            edge = cv2.dilate(edge, np.ones((3, 3), np.uint8))
            out[edge > 0] = (255, 255, 255)
            return np.clip(out, 0, 255).astype(np.uint8)
        return self.compose(frame, a)


def cmd_preview(project_path, out_jpg, view, seconds):
    proj = load_project(project_path)
    cap = cv2.VideoCapture(proj["input"])
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    idx = int(min(max(0, round(seconds * fps)), max(0, total - 1)))
    try:
        pipe = Pipeline(proj, W, H, fps, preview=True)
    except (ImportError, RuntimeError) as e:
        status(str(e))
        sys.exit(1)
    start = max(0, idx - 6)
    cap.set(cv2.CAP_PROP_POS_FRAMES, start)
    frame = a = None
    for step in range(idx - start + 1):
        ok, fr = cap.read()
        if not ok:
            break
        frame = fr
        if view == "original":
            continue
        a = pipe.alpha(fr, start + step)
    cap.release()
    if frame is None:
        status("კადრი ვერ წაიკითხა.")
        sys.exit(1)
    if view == "original" or pipe.is_off(idx):
        out = frame if view in ("result", "original") else pipe.view(frame, a if a is not None else np.ones(frame.shape[:2], np.float32), view)
    else:
        out = pipe.view(frame, a, view)
    vb.imwrite_unicode(out_jpg, out)
    print("[SUCCESS] preview", flush=True)


def cmd_render(project_path):
    proj = load_project(project_path)
    src, dst = proj["input"], proj["output"]
    cap = cv2.VideoCapture(src)
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    try:
        pipe = Pipeline(proj, W, H, fps)
    except (ImportError, RuntimeError) as e:
        status(str(e))
        sys.exit(1)
    status("AI ფონის ანალიზი იწყება...")

    ew, eh = W - W % 2, H - H % 2
    cmd = [vb.find_ffmpeg(), "-y", "-loglevel", "error",
           "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{W}x{H}", "-r", f"{fps:.4f}", "-i", "-",
           "-i", src, "-map", "0:v:0", "-map", "1:a:0?",
           "-vf", f"crop={ew}:{eh}:0:0",
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", dst]
    enc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE)

    q = queue.Queue(maxsize=8)
    threading.Thread(target=vb._prefetch, args=(cap, q), daemon=True).start()
    frame_jpg = proj.get("frame_out")
    t0 = time.time()
    last_report = last_frame = 0.0
    n = 0
    broken = False
    was_off = False
    while True:
        frame = q.get()
        if frame is None:
            break
        if pipe.is_off(n):
            if not was_off:
                pipe.reset()
            was_off = True
            out = frame
        else:
            was_off = False
            a = pipe.alpha(frame, n)
            out = pipe.compose(frame, a)
        try:
            enc.stdin.write(out.tobytes())
        except (BrokenPipeError, OSError):
            broken = True
            break
        n += 1
        now = time.time()
        if now - last_report > 0.4:
            last_report = now
            pct = min(98, int(n / max(total, 1) * 100))
            el = now - t0
            eta = el / max(n, 1) * (total - n)
            print(f"[PROGRESS] {pct}", flush=True)
            print(f"[ETA] {int(eta)}", flush=True)
        if frame_jpg and now - last_frame > 0.9:
            last_frame = now
            try:
                s = 360.0 / max(out.shape[:2])
                vb.imwrite_unicode(frame_jpg, cv2.resize(out, None, fx=s, fy=s, interpolation=cv2.INTER_AREA), 80)
                print(f"[FRAME] {frame_jpg}", flush=True)
            except Exception:
                pass
    try:
        enc.stdin.close()
    except Exception:
        pass
    err = enc.stderr.read().decode("utf-8", "ignore").strip()
    enc.wait()
    cap.release()
    if not broken and enc.returncode == 0 and os.path.exists(dst):
        print("[PROGRESS] 100", flush=True)
        print("[SUCCESS] ვირტუალური ფონი დასრულდა!", flush=True)
    else:
        status("შეცდომა — გამოსავალი ვერ შეიქმნა. " + err[-300:])
        sys.exit(1)


def main():
    a = sys.argv
    try:
        if len(a) >= 6 and a[1] == "preview":
            cmd_preview(a[2], a[3], a[4], float(a[5]))
        elif len(a) >= 3 and a[1] == "render":
            cmd_render(a[2])
        elif len(a) >= 4 and a[1] == "track":
            cmd_track(a[2], a[3])
        else:
            status("გამოყენება: bg_studio.py preview|render|track …")
            sys.exit(1)
    except SystemExit:
        raise
    except Exception as e:
        status(f"შეცდომა: {type(e).__name__}: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
