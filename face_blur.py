"""
face_blur.py — find faces in a video, track them as "people", and blur / pixelate / cover chosen ones.

    python face_blur.py analyze <input.mp4> <analysis.json> [fast|balanced|max]
        → detects + tracks every face, writes <analysis.json> and a thumbnail per person
          (<analysis>.p<ID>.jpg).

    python face_blur.py preview <input.mp4> <analysis.json> <frame_index> '<options json>' <out.jpg>
        → one processed frame.

    python face_blur.py process <input.mp4> <analysis.json> <output.mp4> '<options json>'
        → whole video, audio copied from the source.

Options JSON: {"style": "blur|pixel|black", "strength": 1-100, "pad": 0-100 (percent),
               "shape": "ellipse|rect", "ids": [1,2] | null (null = everyone),
               "edits": {"<id>": [[from_frame, to_frame, 1 = always blur | 0 = never], ...]}}
"""
import sys, os, json, subprocess, urllib.request
import cv2
import numpy as np

try:                                   # Georgian status lines must survive any console code page
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

MODEL_NAME = "face_detection_yunet_2023mar.onnx"
MODEL_URL = ("https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/" + MODEL_NAME)
DETECT_WIDTH = 960          # faces are found on a downscaled frame (fast), boxes are scaled back
FLOW_WIDTH = 640            # optical flow (box tracking between detections) runs on a small gray copy
ANALYSIS_VERSION = 3

# quality presets: detection stride (every n-th frame, flow-tracked in between), detector input width and
# maximum up-scale (small faces), score needed to keep a detection / to start a new person
QUALITY = {
    "fast":     dict(stride=3, width=640,  upscale=1.0, t_det=0.60, t_new=0.70),
    "balanced": dict(stride=2, width=960,  upscale=1.0, t_det=0.50, t_new=0.65),
    "max":      dict(stride=1, width=1280, upscale=1.6, t_det=0.38, t_new=0.55),
}


def load_options(arg):
    """Options come either as inline JSON or as the path of a .json file (safer to pass on Windows)."""
    if os.path.isfile(arg):
        with open(arg, encoding="utf-8-sig") as fh:
            return json.load(fh)
    return json.loads(arg)


def status(msg):
    print(f"[STATUS] {msg}", flush=True)

def _env_ffmpeg():
    p = os.environ.get("FLOW_FFMPEG", "")
    return p if p and os.path.exists(p) else None


def find_model(name):
    """Model file: the app's models folder (FLOW_MODELS_DIR) first, then next to this script.
    If it doesn't exist anywhere, returns the path where a download should be stored."""
    folders = [os.environ.get("FLOW_MODELS_DIR", ""), os.path.dirname(os.path.abspath(__file__))]
    for d in folders:
        if d and os.path.exists(os.path.join(d, name)):
            return os.path.join(d, name)
    target = folders[0] or folders[1]
    try:
        os.makedirs(target, exist_ok=True)
    except Exception:
        target = folders[1]
    return os.path.join(target, name)


def find_ffmpeg():
    _e = _env_ffmpeg()
    if _e:
        return _e
    d = os.path.dirname(os.path.abspath(__file__))
    for p in [os.path.join(d, "ffmpeg.exe"), os.path.abspath("ffmpeg.exe"),
              os.path.abspath(os.path.join(d, "..", "..", "..", "ffmpeg.exe"))]:
        if os.path.exists(p):
            return p
    return "ffmpeg"


def imwrite_unicode(path, img, quality=90):
    ok, buf = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, quality])
    if not ok:
        raise RuntimeError("JPEG encode failed")
    with open(path, "wb") as fh:
        fh.write(buf.tobytes())


# ─────────────────────────── detection ───────────────────────────
class Detector:
    """YuNet (good, small, fast) with a Haar-cascade fallback if the model can't be fetched."""

    def __init__(self):
        self.yunet = None
        self.haar = None
        try:
            path = find_model(MODEL_NAME)
            if not os.path.exists(path):
                status("სახის ამოცნობის მოდელის ჩამოტვირთვა (ერთჯერადი, ~0.2 MB)...")
                tmp = path + ".part"
                urllib.request.urlretrieve(MODEL_URL, tmp)
                os.replace(tmp, path)
            with open(path, "rb") as fh:
                self.model_bytes = np.frombuffer(fh.read(), dtype=np.uint8)
            self._size = (320, 320)
            self.yunet = cv2.FaceDetectorYN.create("onnx", self.model_bytes, np.empty(0, np.uint8),
                                                   self._size, 0.3, 0.3, 5000)   # score filtered by the caller
        except Exception as e:
            status(f"YuNet მიუწვდომელია ({type(e).__name__}) — სტანდარტული დეტექტორი.")
            self.yunet = None
            self.haar = cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml")

    def detect(self, frame, width=None, upscale=1.0):
        """→ list of (x, y, w, h, score) in full-frame pixels."""
        h, w = frame.shape[:2]
        scale = min(upscale, (width or DETECT_WIDTH) / float(w))
        if scale < 1:
            small = cv2.resize(frame, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA)
        elif scale > 1.02:
            small = cv2.resize(frame, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_CUBIC)
        else:
            scale, small = 1.0, frame
        sh, sw = small.shape[:2]
        out = []
        if self.yunet is not None:
            if (sw, sh) != self._size:
                self.yunet.setInputSize((sw, sh))
                self._size = (sw, sh)
            _, faces = self.yunet.detect(small)
            if faces is not None:
                for r in faces:
                    x, y, bw, bh, score = r[0], r[1], r[2], r[3], r[14]
                    out.append((x / scale, y / scale, bw / scale, bh / scale, float(score)))
        else:
            gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
            for (x, y, bw, bh) in self.haar.detectMultiScale(gray, 1.15, 5, minSize=(24, 24)):
                out.append((x / scale, y / scale, bw / scale, bh / scale, 0.8))
        return out


# ─────────────────────────── tracking ───────────────────────────
def iou(a, b):
    ax2, ay2, bx2, by2 = a[0] + a[2], a[1] + a[3], b[0] + b[2], b[1] + b[3]
    iw = max(0, min(ax2, bx2) - max(a[0], b[0]))
    ih = max(0, min(ay2, by2) - max(a[1], b[1]))
    inter = iw * ih
    union = a[2] * a[3] + b[2] * b[3] - inter
    return inter / union if union > 0 else 0.0


class Track:
    """One person. `boxes` holds every covered frame; `real` is the subset where the detector actually saw the face
    (the rest are positions carried forward by optical flow while the face is turned away / hidden / missed)."""

    def __init__(self, tid, frame, box, score):
        self.id = tid
        self.boxes = {frame: box}
        self.real = {frame}
        self.last_real = frame
        self.box = box              # current position (detected or flow-predicted)
        self.pred = box             # where flow thinks the face is on this frame
        self.flow_bad = 0           # frames the carried box could not be trusted
        self.tmpl = None            # appearance of the face at the last real detection
        self.best = score

    def add_real(self, frame, box, score):
        self.boxes[frame] = box
        self.real.add(frame)
        self.last_real = frame
        self.box = box
        self.flow_bad = 0
        self.best = max(self.best, score)

    def add_pred(self, frame):
        self.boxes[frame] = self.pred
        self.box = self.pred


GRID = 5


def patch_vec(gray, box, fs):
    """Small normalised appearance patch of a box (used to notice that the content under a carried box changed:
    cut, dissolve, face left / hidden)."""
    gh, gw = gray.shape[:2]
    x, y, w, h = [v * fs for v in box]
    x0, y0 = max(0, int(x)), max(0, int(y))
    x1, y1 = min(gw, int(x + w)), min(gh, int(y + h))
    if x1 - x0 < 4 or y1 - y0 < 4:
        return None
    v = cv2.resize(gray[y0:y1, x0:x1], (16, 16), interpolation=cv2.INTER_AREA).astype(np.float32).ravel()
    v -= v.mean()
    n = float(np.linalg.norm(v))
    return v / n if n > 1e-3 else None


def advance_tracks(tracks, prev_gray, gray, fs, full_w, full_h):
    """Move every live box along with the image content (Lucas-Kanade on a grid of points inside the face).
    fs = factor from full-frame pixels to the gray image."""
    if not tracks:
        return
    if prev_gray is None:
        for t in tracks:
            t.pred = t.box
        return
    k = GRID * GRID
    pts = []
    for t in tracks:
        x, y, w, h = [v * fs for v in t.box]
        xs = np.linspace(x + 0.2 * w, x + 0.8 * w, GRID)
        ys = np.linspace(y + 0.2 * h, y + 0.8 * h, GRID)
        pts.append(np.array([(px, py) for py in ys for px in xs], np.float32))
    pts = np.concatenate(pts).reshape(-1, 1, 2)
    gh, gw = gray.shape[:2]
    pts[:, 0, 0] = np.clip(pts[:, 0, 0], 0, gw - 1)
    pts[:, 0, 1] = np.clip(pts[:, 0, 1], 0, gh - 1)
    crit = (cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT, 20, 0.03)
    new, st, _ = cv2.calcOpticalFlowPyrLK(prev_gray, gray, pts, None, winSize=(21, 21), maxLevel=3, criteria=crit)
    back, st2, _ = cv2.calcOpticalFlowPyrLK(gray, prev_gray, new, None, winSize=(21, 21), maxLevel=3, criteria=crit)
    fb_err = np.linalg.norm((back - pts).reshape(-1, 2), axis=1)       # forward-backward check: trustworthy points only
    good = (st.ravel() == 1) & (st2.ravel() == 1) & (fb_err < 1.0)
    for i, t in enumerate(tracks):
        sl = slice(i * k, (i + 1) * k)
        ok = good[sl]
        if ok.sum() < 10:                    # flat / blurry / occluded area: hold position, but don't trust it for long
            t.pred = t.box
            t.flow_bad += 1
            continue
        p0 = pts[sl].reshape(-1, 2)[ok]
        p1 = new[sl].reshape(-1, 2)[ok]
        d = np.median(p1 - p0, axis=0)
        keep = np.linalg.norm((p1 - p0) - d, axis=1) < max(2.0, 0.15 * t.box[2] * fs)   # drop points that went elsewhere
        if keep.sum() >= 6:
            p0, p1 = p0[keep], p1[keep]
            d = np.median(p1 - p0, axis=0)
        r0 = np.linalg.norm(p0 - p0.mean(0), axis=1).mean()
        r1 = np.linalg.norm(p1 - p1.mean(0), axis=1).mean()
        s = float(np.clip(r1 / r0, 0.93, 1.07)) if r0 > 1.0 else 1.0
        cx = t.box[0] + t.box[2] / 2 + d[0] / fs
        cy = t.box[1] + t.box[3] / 2 + d[1] / fs
        w, h = t.box[2] * s, t.box[3] * s
        t.pred = (cx - w / 2, cy - h / 2, w, h)
        if t.tmpl is not None:                                 # does it still look like the face we were following?
            pv = patch_vec(gray, t.pred, fs)
            if pv is None or float(np.dot(pv, t.tmpl)) < 0.25:
                t.flow_bad += 3
        if cx + w / 2 < 0 or cx - w / 2 > full_w or cy + h / 2 < 0 or cy - h / 2 > full_h:   # fully left the picture
            t.flow_bad += 999


def match(tracks, dets, frame):
    """Greedy matching of detections to the (flow-predicted) track boxes. → (list of (track, det idx), unmatched det idx)."""
    pairs = []
    for t in tracks:
        tb = t.pred
        cx_t, cy_t = tb[0] + tb[2] / 2, tb[1] + tb[3] / 2
        miss = frame - t.last_real
        for di, d in enumerate(dets):
            ov = iou(tb, d[:4])
            cx_d, cy_d = d[0] + d[2] / 2, d[1] + d[3] / 2
            dist = ((cx_t - cx_d) ** 2 + (cy_t - cy_d) ** 2) ** 0.5
            size = max(tb[2], tb[3], d[2], d[3])
            ratio = max(tb[2], d[2]) / max(1e-6, min(tb[2], d[2]))
            if ratio > 2.2:
                continue
            if ov > 0.15 or dist < size * (0.5 + 0.04 * min(miss, 15)):
                pairs.append((ov * 2 - dist / (size + 1e-6), t, di))
    pairs.sort(key=lambda p: -p[0])
    taken, used, out = set(), set(), []
    for _, t, di in pairs:
        if id(t) in taken or di in used:
            continue
        taken.add(id(t)); used.add(di)
        out.append((t, di))
    return out, [i for i in range(len(dets)) if i not in used]


def merge_tracks(tracks, fps, cuts=(), max_gap_s=0.8):
    """Stitch fragments of the same person (lost for a second, came back): spatial + size + time proximity.
    Rounds of greedy matching over time-windowed candidates (fast even with hundreds of faces)."""
    import bisect
    max_gap = int(fps * max_gap_s)
    while True:
        tracks.sort(key=lambda t: min(t.boxes))
        starts = [min(t.boxes) for t in tracks]
        ends = [max(t.boxes) for t in tracks]
        order_end = sorted(range(len(tracks)), key=lambda i: ends[i])
        end_vals = [ends[i] for i in order_end]
        cands = []
        for bi, b in enumerate(tracks):
            fb_ = starts[bi]
            lo = bisect.bisect_left(end_vals, fb_ - max_gap)
            hi = bisect.bisect_left(end_vals, fb_)            # ends strictly before b starts
            bb = b.boxes[fb_]
            cb = (bb[0] + bb[2] / 2, bb[1] + bb[3] / 2)
            for oi in order_end[lo:hi]:
                a = tracks[oi]
                if a is b:
                    continue
                gap = fb_ - ends[oi]
                ci = bisect.bisect_right(cuts, ends[oi])
                if ci < len(cuts) and cuts[ci] <= fb_ and gap > 4:   # a cut between them: different shot, different person
                    continue                                          # (unless it is only a 1-3 frame flash in between)
                ba = a.boxes[ends[oi]]
                ca = (ba[0] + ba[2] / 2, ba[1] + ba[3] / 2)
                dist = ((ca[0] - cb[0]) ** 2 + (ca[1] - cb[1]) ** 2) ** 0.5
                size = max(ba[3], bb[3])
                ratio = size / max(1e-6, min(ba[3], bb[3]))
                if ratio > 1.7 or dist > size * (0.7 + 0.55 * gap / fps):
                    continue
                cands.append((dist / size + 0.3 * gap / fps, oi, bi))
        if not cands:
            return tracks
        cands.sort()
        used_a, used_b, merged, drop = set(), set(), 0, set()
        for _, oi, bi in cands:
            if oi in used_a or bi in used_b or oi in used_b or bi in used_a:
                continue
            used_a.add(oi); used_b.add(bi)
            a, b = tracks[oi], tracks[bi]
            a.boxes.update(b.boxes)
            a.real |= b.real
            a.best = max(a.best, b.best)
            a.last_real = max(a.last_real, b.last_real)
            drop.add(bi)
            merged += 1
        tracks = [t for i, t in enumerate(tracks) if i not in drop]

def _edge_dist(box, W, H):
    """Smallest gap (in face sizes) between the box and a picture edge; negative = already sticks out."""
    x, y, w, h = box
    size = max(w, h, 1.0)
    return min(x, y, W - (x + w), H - (y + h)) / size


def _extend(boxes, anchor, direction, total_frames, fps, W, H, hold, cuts=()):
    """Frames before the first / after the last sighting. Near a picture edge the box keeps moving the way the
    face was going until it is completely outside (or, going the other way, slides in from outside); elsewhere
    it just stays put for a moment."""
    out = {}
    b0 = boxes[anchor]
    near_edge = _edge_dist(b0, W, H) < 0.25
    v = np.zeros(2, np.float32)
    if near_edge:
        ks = sorted(boxes)
        pts = [k for k in ks if abs(k - anchor) <= 8 and k != anchor]
        if pts:
            k = max(pts, key=lambda q: abs(q - anchor))
            d = abs(k - anchor)
            # velocity along the direction we extend in (time forward for +1, backward for -1)
            v = np.array([(boxes[k][0] + boxes[k][2] / 2) - (b0[0] + b0[2] / 2),
                          (boxes[k][1] + boxes[k][3] / 2) - (b0[1] + b0[3] / 2)], np.float32) / (k - anchor) * direction
            v = np.clip(v, -0.15 * max(b0[2], b0[3]), 0.15 * max(b0[2], b0[3]))
    max_steps = hold
    if near_edge and float(np.linalg.norm(v)) > 0.3:
        probe = (b0[0] + v[0] * 4, b0[1] + v[1] * 4, b0[2], b0[3])
        if _edge_dist(probe, W, H) < _edge_dist(b0, W, H):          # really heading out of the picture
            max_steps = max(3, int(fps * 0.2))             # only a few frames: the sliver of face still in the picture
    cut_set = set(cuts)
    x, y, w, h = b0
    for j in range(1, max_steps + 1):
        f = anchor + direction * j
        if f < 0 or f >= total_frames:
            break
        if f in cut_set or (direction < 0 and (f + 1) in cut_set):         # never carry a box across a cut
            break
        x += float(v[0]); y += float(v[1])
        if x + w < 0 or x > W or y + h < 0 or y > H:       # completely outside → nothing left to cover
            break
        out[f] = (x, y, w, h)
    return out


def finalize(track, total_frames, fps, frame_size, pre=4, post=4, cuts=()):
    """Fill gaps by interpolation, extend a little before/after, smooth the boxes."""
    max_fill = int(fps * 0.7)
    frames = sorted(track.boxes)
    full = {}
    for a, b in zip(frames, frames[1:]):
        full[a] = track.boxes[a]
        gap = b - a
        if 1 < gap <= max_fill:
            ba, bb = track.boxes[a], track.boxes[b]
            for k in range(1, gap):
                t = k / gap
                full[a + k] = tuple(ba[i] * (1 - t) + bb[i] * t for i in range(4))
    full[frames[-1]] = track.boxes[frames[-1]]
    first, last = frames[0], frames[-1]
    W, H = frame_size
    full.update(_extend(track.boxes, first, -1, total_frames, fps, W, H, pre, cuts))     # face appears
    full.update(_extend(track.boxes, last, +1, total_frames, fps, W, H, post, cuts))     # face is lost
    ks = sorted(full)
    arr = np.array([full[k] for k in ks], dtype=np.float32)
    if len(arr) >= 5:                          # moving-average smoothing (edge-padded)
        kernel = np.ones(5, np.float32) / 5
        pad = np.pad(arr, ((2, 2), (0, 0)), mode="edge")
        arr = np.stack([np.convolve(pad[:, i], kernel, mode="valid") for i in range(4)], axis=1)
    return {int(k): [float(v) for v in arr[i]] for i, k in enumerate(ks)}


# ─────────────────────────── effects ───────────────────────────
def odd(n):
    n = int(max(3, n))
    return n if n % 2 else n + 1


def apply_effect(frame, box, style, strength, pad_pct, shape):
    h, w = frame.shape[:2]
    x, y, bw, bh = box
    padx, pady = bw * pad_pct / 100.0, bh * pad_pct / 100.0
    # the full effect area, even where it sticks out of the picture: size and shape stay the same while a face
    # enters / leaves, only the visible part of the blur is pasted
    x0 = int(round(x - padx)); y0 = int(round(y - pady))
    x1 = int(round(x + bw + padx)); y1 = int(round(y + bh + pady))
    rw, rh = x1 - x0, y1 - y0
    if rw < 4 or rh < 4 or x1 <= 0 or y1 <= 0 or x0 >= w or y0 >= h:
        return
    vx0, vy0, vx1, vy1 = max(0, x0), max(0, y0), min(w, x1), min(h, y1)        # visible part
    inside = (vx0, vy0, vx1, vy1) == (x0, y0, x1, y1)
    if inside:
        roi = frame[y0:y1, x0:x1]
    else:                                   # edge pixels are repeated outward so the blur looks natural at the border
        part = frame[vy0:vy1, vx0:vx1]
        roi = cv2.copyMakeBorder(part, vy0 - y0, y1 - vy1, vx0 - x0, x1 - vx1, cv2.BORDER_REPLICATE)
    s = max(1, min(100, strength)) / 100.0

    if style == "black":
        eff = np.zeros_like(roi)
    elif style == "pixel":
        cells = int(max(3, round(18 - 15 * s)))          # stronger → fewer, bigger blocks
        cells_y = max(2, int(round(cells * rh / float(rw))))
        small = cv2.resize(roi, (cells, cells_y), interpolation=cv2.INTER_AREA)
        eff = cv2.resize(small, (rw, rh), interpolation=cv2.INTER_NEAREST)
    else:
        # blur a small copy and scale it back up: looks the same, ~10x faster than a huge kernel on full size
        f = max(1.0, max(rw, rh) / 96.0)
        sw, sh = max(2, int(rw / f)), max(2, int(rh / f))
        small = cv2.resize(roi, (sw, sh), interpolation=cv2.INTER_AREA)
        k = odd(max(sw, sh) * (0.18 + 0.55 * s))
        small = cv2.GaussianBlur(small, (k, k), 0)
        small = cv2.GaussianBlur(small, (k, k), 0)
        eff = cv2.resize(small, (rw, rh), interpolation=cv2.INTER_CUBIC)

    if shape != "rect":
        mask = np.zeros((rh, rw), np.float32)
        cv2.ellipse(mask, (rw // 2, rh // 2), (max(1, int(rw * 0.5) - 1), max(1, int(rh * 0.5) - 1)), 0, 0, 360, 1.0, -1)
        fk = odd(min(rw, rh) * 0.12)
        mask = cv2.GaussianBlur(mask, (fk, fk), 0)[..., None]
        eff = (eff.astype(np.float32) * mask + roi.astype(np.float32) * (1 - mask)).astype(np.uint8)

    ox, oy = vx0 - x0, vy0 - y0
    frame[vy0:vy1, vx0:vx1] = eff[oy:oy + (vy1 - vy0), ox:ox + (vx1 - vx0)]

def _edit_state(edits, f):
    """Last manual edit that covers frame f: 1 = force cover, 0 = force no blur, None = automatic."""
    state = None
    for a, b, inc in edits:
        if a <= f <= b:
            state = 1 if inc else 0
    return state


def _nearest_box(t, f):
    import bisect
    fr = t.get("_fr")
    if fr is None:
        fr = t["_fr"] = sorted(int(k) for k in t["boxes"])
    if not fr:
        return None
    i = bisect.bisect_left(fr, f)
    cands = [fr[j] for j in (i - 1, i) if 0 <= j < len(fr)]
    best = min(cands, key=lambda k: abs(k - f))
    return t["boxes"][str(best)]


def boxes_at(analysis, frame_idx, ids, edits=None):
    out = []
    key = str(frame_idx)
    for t in analysis["tracks"]:
        if ids is not None and t["id"] not in ids:
            continue
        ed = (edits or {}).get(str(t["id"]))
        state = _edit_state(ed, frame_idx) if ed else None
        if state == 0:
            continue
        b = t["boxes"].get(key)
        if not b and state == 1:
            b = _nearest_box(t, frame_idx)
        if b:
            out.append(b)
    return out

def process_frame(frame, analysis, frame_idx, opt):
    ids = opt.get("ids")
    ids = set(int(i) for i in ids) if ids is not None else None
    for b in boxes_at(analysis, frame_idx, ids, opt.get("edits")):
        apply_effect(frame, b, opt.get("style", "blur"), int(opt.get("strength", 60)),
                     float(opt.get("pad", 30)), opt.get("shape", "ellipse"))
    return frame


# ─────────────────────────── commands ───────────────────────────
def cmd_analyze(inp, out_json, quality="balanced"):
    q = QUALITY.get(quality, QUALITY["balanced"])
    cap = cv2.VideoCapture(inp)
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    det = Detector()
    status("სახეების ძებნა…")

    fs = min(1.0, FLOW_WIDTH / float(max(width, 1)))
    stride = q["stride"]
    max_miss = int(max(10, fps * 0.9))          # a face may stay unseen this long before the person is closed
    flow_limit = int(max(3, fps * 0.3))         # flow untrusted for this long → stop carrying the box
    tail_keep = int(max(2, fps * 0.12))         # ... and this much of the flow-carried tail is kept
    active, done = [], []
    cuts = []
    next_id = 1
    prev_gray = None
    n = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        gray = cv2.cvtColor(cv2.resize(frame, None, fx=fs, fy=fs, interpolation=cv2.INTER_AREA) if fs < 1 else frame,
                            cv2.COLOR_BGR2GRAY)
        cut = False
        if prev_gray is not None and prev_gray.shape == gray.shape:
            hc = cv2.compareHist(cv2.calcHist([prev_gray], [0], None, [32], [0, 256]),
                                 cv2.calcHist([gray], [0], None, [32], [0, 256]), cv2.HISTCMP_CORREL)
            cut = hc < 0.45 or (hc < 0.8 and cv2.absdiff(prev_gray, gray).mean() > 40)
        if cut:                                  # hard cut: nobody can be carried over, boxes of the old shot end here
            for t in active:
                for f in [f for f in t.boxes if f > t.last_real + 2]:
                    del t.boxes[f]
                done.append(t)
            active = []
            prev_gray = None
            cuts.append(n)
        advance_tracks(active, prev_gray, gray, fs, width, height)
        prev_gray = gray

        matched = []
        if n % stride == 0:
            dets = [d for d in det.detect(frame, q["width"], q["upscale"]) if d[4] >= q["t_det"]]
            pairs, free = match(active, dets, n)
            for t, di in pairs:
                t.add_real(n, tuple(dets[di][:4]), dets[di][4])
                t.tmpl = patch_vec(gray, t.box, fs)
                matched.append(t)
            for di in free:
                d = dets[di]
                if d[4] < q["t_new"]:
                    continue
                if any(iou(t.pred, d[:4]) > 0.45 for t in active):     # same face as a live (drifted) track
                    continue
                t = Track(next_id, n, tuple(d[:4]), d[4])
                t.tmpl = patch_vec(gray, t.box, fs)
                next_id += 1
                active.append(t)
                matched.append(t)
        for t in active:
            if t.last_real != n:
                t.add_pred(n)

        still = []
        for t in active:
            if n - t.last_real > max_miss or t.flow_bad > flow_limit:
                for f in [f for f in t.boxes if f > t.last_real + tail_keep]:   # drop the drifting end of the tail
                    del t.boxes[f]
                done.append(t)
            else:
                still.append(t)
        active = still

        n += 1
        if n % 10 == 0:
            print(f"[PROGRESS] {min(90, int(n / max(total, 1) * 90))}", flush=True)
    cap.release()
    status("ვასრულებ ანალიზს…")
    print("[PROGRESS] 92", flush=True)
    for t in active:
        for f in [f for f in t.boxes if f > t.last_real + tail_keep]:
            del t.boxes[f]
    done.extend(active)
    total = max(total, n)

    # stitch fragments, then drop blips (very short, low-confidence = almost always false positives)
    done = merge_tracks(done, fps, cuts)
    min_real = max(2, int(round(5.0 / stride)))
    kept = [t for t in done if len(t.real) >= min_real or t.best >= 0.88]
    kept.sort(key=lambda t: min(t.boxes))

    people = []
    for new_id, t in enumerate(kept, 1):
        boxes = finalize(t, total, fps, (width, height), pre=max(4, int(fps * 0.15)), post=max(3, int(fps * 0.12)), cuts=cuts)
        sizes = sorted(b[3] for b in boxes.values())
        size = sizes[len(sizes) // 2] / float(height)           # typical face height as a fraction of the frame
        best_frame = max(t.real, key=lambda f: t.boxes[f][2] * t.boxes[f][3])
        people.append({
            "id": new_id,
            "first": min(boxes), "last": max(boxes), "count": len(t.real),
            "best_frame": best_frame, "size": round(size, 4), "score": round(float(t.best), 3),
            "boxes": {str(k): v for k, v in boxes.items()},
        })

    # main people = big enough and on screen long enough (at most 12, biggest/longest first); the rest is "crowd"
    for p in people:
        dur = (p["last"] - p["first"] + 1) / fps
        p["rank"] = p["size"] * min(dur, 8.0) ** 0.5
        p["minor"] = not (p["size"] >= 0.04 and dur >= 0.7)
    for p in sorted([p for p in people if not p["minor"]], key=lambda p: -p["rank"])[12:]:
        p["minor"] = True

    # thumbnails (a crop of the biggest, clearest appearance of each person)
    cap = cv2.VideoCapture(inp)
    base = out_json
    for p in people:
        if p["minor"]:
            continue                                             # the crowd card has no portraits
        cap.set(cv2.CAP_PROP_POS_FRAMES, p["best_frame"])
        ok, fr = cap.read()
        if not ok:
            continue
        b = p["boxes"].get(str(p["best_frame"])) or next(iter(p["boxes"].values()))
        x, y, bw, bh = b
        m = 0.35
        x0 = int(max(0, x - bw * m)); y0 = int(max(0, y - bh * m))
        x1 = int(min(width, x + bw * (1 + m))); y1 = int(min(height, y + bh * (1 + m)))
        crop = fr[y0:y1, x0:x1]
        if crop.size == 0:
            continue
        s = 160.0 / max(crop.shape[:2])
        crop = cv2.resize(crop, (max(1, int(crop.shape[1] * s)), max(1, int(crop.shape[0] * s))))
        thumb = f"{base}.p{p['id']}.jpg"
        imwrite_unicode(thumb, crop)
        p["thumb"] = thumb
    cap.release()

    data = {"version": ANALYSIS_VERSION, "quality": quality, "fps": fps, "width": width, "height": height,
            "frames": total, "cuts": cuts, "tracks": people}
    with open(out_json, "w", encoding="utf-8") as fh:
        json.dump(data, fh)
    print("[PROGRESS] 100", flush=True)
    print(f"[SUCCESS] {len(people)} ადამიანი", flush=True)


def cmd_preview(inp, analysis_path, frame_idx, opt_json, out_jpg):
    with open(analysis_path, encoding="utf-8") as fh:
        analysis = json.load(fh)
    opt = load_options(opt_json)
    cap = cv2.VideoCapture(inp)
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(frame_idx))
    ok, frame = cap.read()
    cap.release()
    if not ok:
        status("კადრი ვერ წაიკითხა.")
        sys.exit(1)
    process_frame(frame, analysis, int(frame_idx), opt)
    imwrite_unicode(out_jpg, frame, 92)
    print("[SUCCESS] preview", flush=True)


def cmd_process(inp, analysis_path, out_path, opt_json):
    with open(analysis_path, encoding="utf-8") as fh:
        analysis = json.load(fh)
    opt = load_options(opt_json)
    cap = cv2.VideoCapture(inp)
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    ew, eh = w - w % 2, h - h % 2

    cmd = [find_ffmpeg(), "-y", "-loglevel", "error",
           "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{w}x{h}", "-r", f"{fps:.4f}", "-i", "-",
           "-i", inp, "-map", "0:v:0", "-map", "1:a:0?",
           "-vf", f"crop={ew}:{eh}:0:0",
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", out_path]
    enc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    status("დაბუნდოვნება მიმდინარეობს…")

    n = 0
    broken = False
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        process_frame(frame, analysis, n, opt)
        try:
            enc.stdin.write(frame.tobytes())
        except (BrokenPipeError, OSError):
            broken = True
            break
        n += 1
        if n % 10 == 0:
            print(f"[PROGRESS] {min(98, int(n / max(total, 1) * 100))}", flush=True)
    cap.release()
    try:
        enc.stdin.close()
    except Exception:
        pass
    err = enc.stderr.read().decode("utf-8", "ignore").strip()
    enc.wait()

    if not broken and enc.returncode == 0 and os.path.exists(out_path):
        print("[PROGRESS] 100", flush=True)
        print("[SUCCESS] დაბუნდოვნება დასრულდა!", flush=True)
    else:
        status("შეცდომა — გამოსავალი ვერ შეიქმნა. " + err[-300:])
        sys.exit(1)


def main():
    a = sys.argv
    try:
        if len(a) >= 4 and a[1] == "analyze":
            cmd_analyze(a[2], a[3], a[4] if len(a) > 4 else "balanced")
        elif len(a) >= 7 and a[1] == "preview":
            cmd_preview(a[2], a[3], a[4], a[5], a[6])
        elif len(a) >= 6 and a[1] == "process":
            cmd_process(a[2], a[3], a[4], a[5])
        else:
            status("გამოყენება: face_blur.py analyze|preview|process …")
            sys.exit(1)
    except SystemExit:
        raise
    except Exception as e:
        status(f"შეცდომა: {type(e).__name__}: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
