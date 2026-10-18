"""
virtual_bg.py — AI Virtual Background (no green screen needed)

Supports both old MediaPipe API (< 0.10) and new Tasks API (>= 0.10).

Usage:
    python virtual_bg.py <input.mp4> <mode> <param> <output.mp4> [invert]      process a video
    python virtual_bg.py preview <input.mp4> <mode> <param> <out.jpg> [invert]  one processed frame (middle of the video)
    python virtual_bg.py webcam  <mode> <param> "" [invert]                     live camera window (ESC closes)

    mode   = blur | color | image | video
    param  = blur strength (e.g. 35) | #RRGGBB | path to image/video
    invert = "1" → the person is replaced and the background stays

Optional flags (anywhere): --opts <file.json>   --t <seconds> (preview: which moment to show)
Options JSON: {"quality": "fast|balanced|max", "solidify": 0-100, "defringe": 0-100, "smooth": 0-100,
               "grow": -10..10 (edge in/out, % of height*0.1), "feather": 0-100,
               "off": [[from_sec, to_sec], ...]   ← moments where the original picture stays,
               "people": {"analysis": "<analysis.json of face_blur.py>", "keep": [1, 2]}  ← only these people stay}
"""
import sys, os, subprocess, urllib.request, tempfile, threading, queue
import cv2
import numpy as np

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
    script_dir = os.path.dirname(os.path.abspath(__file__))
    for p in [os.path.join(script_dir, "ffmpeg.exe"),
              os.path.abspath("ffmpeg.exe"),
              os.path.abspath(os.path.join(script_dir, "..", "..", "..", "ffmpeg.exe"))]:
        if os.path.exists(p):
            return p
    return "ffmpeg"


# ── Unicode-safe image I/O (cv2.imread/imwrite break on non-ASCII Windows paths) ──
def imread_unicode(path):
    try:
        data = np.fromfile(path, dtype=np.uint8)
        return cv2.imdecode(data, cv2.IMREAD_COLOR)
    except Exception:
        return None


def imwrite_unicode(path, img, quality=92):
    ok, buf = cv2.imencode('.jpg', img, [cv2.IMWRITE_JPEG_QUALITY, quality])
    if not ok:
        raise RuntimeError("JPEG encode failed")
    with open(path, 'wb') as fh:
        fh.write(buf.tobytes())


def ascii_temp_path(suffix):
    """A temp file path made only of ASCII characters (OpenCV's VideoWriter can't open others)."""
    for base in (tempfile.gettempdir(), os.environ.get("PUBLIC", ""), r"C:\Users\Public", r"C:\Windows\Temp"):
        if base and all(ord(ch) < 128 for ch in base) and os.path.isdir(base):
            return os.path.join(base, f"flow_vbg_{os.getpid()}{suffix}")
    return os.path.join(tempfile.gettempdir(), f"flow_vbg_{os.getpid()}{suffix}")


# ── Detect which MediaPipe API is available ───────────────────────────────
def get_segmenter():
    """
    Returns (segment_fn, close_fn). segment_fn: frame_bgr → mask (float32 HxW, person ≈ 1.0)
    Tries new Tasks API first; falls back to old mp.solutions API.
    """
    try:
        import mediapipe as mp
    except ImportError:
        raise ImportError(
            "mediapipe არ არის დაყენებული.\n\n"
            "გაუშვით ტერმინალში:\n"
            "  pip install mediapipe"
        )

    # ── Attempt new Tasks API (mediapipe >= 0.10) ──────────────────────
    try:
        from mediapipe.tasks import python as mp_python
        from mediapipe.tasks.python import vision as mp_vision

        MODEL_URL = (
            "https://storage.googleapis.com/mediapipe-models/"
            "image_segmenter/selfie_segmenter/float16/1/selfie_segmenter.tflite"
        )
        model_path = find_model("selfie_segmenter.tflite")
        if not os.path.exists(model_path):
            print("[STATUS] მოდელის ჩამოტვირთვა (ერთჯერადი, ~2 MB)...", flush=True)
            urllib.request.urlretrieve(MODEL_URL, model_path)
            print("[STATUS] მოდელი ჩამოიტვირთა.", flush=True)

        # MediaPipe's native loader can't read non-ASCII paths → pass the model as bytes
        with open(model_path, "rb") as fh:
            model_bytes = fh.read()
        base_opts = mp_python.BaseOptions(model_asset_buffer=model_bytes)
        seg_opts = mp_vision.ImageSegmenterOptions(
            base_options=base_opts,
            output_category_mask=False,
            output_confidence_masks=True,
            running_mode=mp_vision.RunningMode.IMAGE,
        )
        segmenter = mp_vision.ImageSegmenter.create_from_options(seg_opts)

        def segment_tasks(frame_bgr):
            rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
            mp_img = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
            result = segmenter.segment(mp_img)
            return result.confidence_masks[0].numpy_view().copy()

        return segment_tasks, lambda: segmenter.close()

    except Exception:
        pass  # fall through to old API

    # ── Fall back to old mp.solutions API (mediapipe < 0.10) ──────────
    try:
        import mediapipe as mp
        segmenter = mp.solutions.selfie_segmentation.SelfieSegmentation(model_selection=1)

        def segment_solutions(frame_bgr):
            rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
            rgb.flags.writeable = False
            return segmenter.process(rgb).segmentation_mask

        return segment_solutions, lambda: segmenter.close()

    except Exception as e:
        raise RuntimeError(
            f"MediaPipe API ვერ ინიციალიზდა: {e}\n\n"
            "სცადეთ:\n"
            "  pip install mediapipe --upgrade"
        )


# ── Matting engines ────────────────────────────────────────────────────────
# Preferred: Robust Video Matting (ONNX) — made for video, keeps temporal memory, fine hair/finger detail.
# Fallback : MediaPipe selfie segmenter (small 256x256 model, coarser edges).
RVM_NAME = "rvm_mobilenetv3_fp32.onnx"
RVM_URL = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/" + RVM_NAME


class RvmMatte:
    name = "rvm"

    def __init__(self, session, width, internal=480):
        self.sess, self.width, self.internal = session, width, internal
        self.cut = False
        self.reset()
        self._prev_small = None

    def reset(self):
        self.rec = [np.zeros((1, 1, 1, 1), np.float32) for _ in range(4)]

    def alpha(self, frame):
        h, w = frame.shape[:2]
        W = min(self.width, w)
        W -= W % 2
        H = max(2, int(round(W * h / w / 2)) * 2)
        small = cv2.resize(frame, (W, H), interpolation=cv2.INTER_AREA)
        # a hard cut → forget the previous scene, otherwise the old person "ghosts" into the new shot
        g = cv2.cvtColor(cv2.resize(small, (64, 36)), cv2.COLOR_BGR2GRAY).astype(np.int16)
        self.cut = False
        if self._prev_small is not None and np.abs(g - self._prev_small).mean() > 45:
            self.reset()
            self.cut = True
        self._prev_small = g
        x = cv2.cvtColor(small, cv2.COLOR_BGR2RGB).astype(np.float32).transpose(2, 0, 1)[None] / 255.0
        ratio = np.array([min(1.0, float(self.internal) / W)], np.float32)
        out = self.sess.run(None, {"src": x, "r1i": self.rec[0], "r2i": self.rec[1], "r3i": self.rec[2],
                                   "r4i": self.rec[3], "downsample_ratio": ratio})
        self.rec = list(out[2:6])
        a = out[1][0, 0]
        if a.shape != (h, w):
            a = cv2.resize(a, (w, h), interpolation=cv2.INTER_LINEAR)
        # remove faint haze around the person, keep the soft edge
        return np.clip((a - 0.04) / 0.92, 0.0, 1.0).astype(np.float32)

    def close(self):
        pass


class MpMatte:
    name = "mediapipe"

    def __init__(self, segment_fn, close_fn):
        self.segment_fn, self.close_fn = segment_fn, close_fn

    cut = False

    def reset(self):
        pass

    def alpha(self, frame):
        m = self.segment_fn(frame)
        m = np.squeeze(m)
        if m.shape[:2] != frame.shape[:2]:
            m = cv2.resize(m, (frame.shape[1], frame.shape[0]))
        m = np.clip((m - 0.35) / 0.40, 0.0, 1.0)
        return cv2.GaussianBlur(m, (9, 9), 0).astype(np.float32)

    def close(self):
        self.close_fn()


QUALITY = {"fast": (640, 384), "balanced": (960, 480), "max": (1280, 640)}    # (input width, matting resolution)


def get_matte(width=960, internal=480):
    """Best available matting engine. Prints a [STATUS] line if it has to fall back."""
    try:
        import onnxruntime as ort
        model_path = find_model(RVM_NAME)
        if not os.path.exists(model_path):
            print("[STATUS] AI მოდელის ჩამოტვირთვა (ერთჯერადი, ~15 MB)...", flush=True)
            tmp = model_path + ".part"
            urllib.request.urlretrieve(RVM_URL, tmp)
            os.replace(tmp, model_path)
        with open(model_path, "rb") as fh:
            data = fh.read()
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        sess = ort.InferenceSession(data, opts, providers=["CPUExecutionProvider"])
        return RvmMatte(sess, width, internal)
    except Exception as e:
        print(f"[STATUS] მაღალი ხარისხის მოდელი მიუწვდომელია ({type(e).__name__}) — სტანდარტული რეჟიმი.", flush=True)
        seg, close = get_segmenter()
        return MpMatte(seg, close)

# ── Options + matte refinement ──────────────────────────────────────────────
DEFAULT_OPTS = {"quality": "balanced", "solidify": 60, "defringe": 60, "smooth": 30, "grow": 0, "feather": 0, "off": [],
                "people": None}            # {"analysis": "<face_blur json>", "keep": [ids]} → only these people stay


def make_selector(opts):
    pe = opts.get("people")
    if not pe or not pe.get("analysis") or not os.path.isfile(pe["analysis"]):
        return None
    try:
        return PersonSelector(pe["analysis"], pe.get("keep", []))
    except Exception as e:
        print(f"[STATUS] ადამიანების არჩევა ვერ ჩაიტვირთა: {e}", flush=True)
        return None


def load_opts(path):
    o = dict(DEFAULT_OPTS)
    if path and os.path.isfile(path):
        try:
            import json
            with open(path, encoding="utf-8-sig") as fh:
                o.update(json.load(fh))
        except Exception as e:
            print(f"[STATUS] პარამეტრების ფაილი ვერ წაიკითხა: {e}", flush=True)
    return o


def solidify(alpha, amount):
    """Fill the holes the matting leaves INSIDE the person (jersey / hair colour close to the background)."""
    if amount <= 0:
        return alpha
    h, w = alpha.shape
    sc = 480.0 / max(h, w)                       # a cheap mask operation: work on a small copy
    a = cv2.resize(alpha, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA) if sc < 1 else alpha
    core = (a > 0.18).astype(np.uint8)
    inv = (1 - core).astype(np.uint8)
    ff = inv.copy()
    mask = np.zeros((inv.shape[0] + 2, inv.shape[1] + 2), np.uint8)
    for (x, y) in [(0, 0), (inv.shape[1] - 1, 0), (0, inv.shape[0] - 1), (inv.shape[1] - 1, inv.shape[0] - 1)]:
        if ff[y, x] == 1:
            cv2.floodFill(ff, mask, (x, y), 2)
    holes = (ff == 1).astype(np.uint8)           # "background" pixels fully enclosed by the person
    n, lab, stats, _ = cv2.connectedComponentsWithStats(holes, connectivity=4)
    maxarea = a.shape[0] * a.shape[1] * 0.004 * (amount / 100.0) * 1.6
    for i in range(1, n):
        if stats[i, cv2.CC_STAT_AREA] <= maxarea:
            core[lab == i] = 1
    r = max(1, int(round(a.shape[0] * 0.012)))
    inner = cv2.erode(core, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1)))
    inner = cv2.GaussianBlur(inner.astype(np.float32), (0, 0), r * 0.6)
    inner = cv2.resize(inner, (w, h), interpolation=cv2.INTER_LINEAR) if sc < 1 else inner
    return np.maximum(alpha, inner * min(1.0, amount / 50.0))


def defringe(frame, alpha, strength):
    """Take the OLD background colour out of the soft edge, so no halo shows on the new background."""
    if strength <= 0:
        return frame
    h, w = alpha.shape
    soft = (alpha > 0.02) & (alpha < 0.98)
    ys, xs = np.where(soft[::4, ::4])                      # where is there any soft edge at all? (coarse look)
    if len(ys) == 0:
        return frame
    pad = max(8, int(h * 0.04))
    y0, y1 = max(0, ys.min() * 4 - pad), min(h, ys.max() * 4 + pad + 4)
    x0, x1 = max(0, xs.min() * 4 - pad), min(w, xs.max() * 4 + pad + 4)
    fr, al = frame[y0:y1, x0:x1], alpha[y0:y1, x0:x1]
    ch, cw = al.shape
    sc = 0.3
    sm = cv2.resize(fr, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA).astype(np.float32)
    am = cv2.resize(al, None, fx=sc, fy=sc, interpolation=cv2.INTER_AREA)
    inv = 1 - am
    k = max(3, int(h * sc * 0.03)) | 1
    num = cv2.blur(sm * inv[..., None], (k, k))
    den = cv2.blur(inv, (k, k))[..., None] + 1e-3
    bg_old = num / den                                   # what the old background looked like next to the edge
    F = np.clip((sm - (1 - am)[..., None] * bg_old) / np.clip(am, 0.05, 1)[..., None], 0, 255)
    band = ((am > 0.02) & (am < 0.98)).astype(np.float32)
    band = cv2.blur(band, (5, 5)) * (strength / 100.0)
    band = cv2.resize(band, (cw, ch), interpolation=cv2.INTER_LINEAR)[..., None]
    F = cv2.resize(F, (cw, ch), interpolation=cv2.INTER_LINEAR)
    out = frame.copy()
    out[y0:y1, x0:x1] = (fr.astype(np.float32) * (1 - band) + F * band).astype(np.uint8)
    return out

class Refiner:
    """alpha post-processing: edge position, feather, filled holes, calmer flicker."""

    def __init__(self, o):
        self.o = o
        self.prev = None

    def reset(self):
        self.prev = None

    def alpha(self, frame, a):
        o = self.o
        h = a.shape[0]
        grow = float(o.get("grow", 0))
        if abs(grow) >= 0.5:                              # push the edge outwards (+) / inwards (-)
            r = max(1, int(round(h * 0.0012 * abs(grow))))
            kern = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1))
            a = cv2.dilate(a, kern) if grow > 0 else cv2.erode(a, kern)
        feather = float(o.get("feather", 0))
        if feather > 0:
            sg = max(0.6, h * 0.0016 * feather / 10.0)
            a = cv2.GaussianBlur(a, (0, 0), sg)
        a = solidify(a, float(o.get("solidify", 0)))
        sm = float(o.get("smooth", 0)) / 100.0
        if sm > 0 and self.prev is not None and self.prev.shape == a.shape:
            base = 1.0 - 0.6 * sm                         # how much of the NEW frame is taken in static areas
            diff = np.abs(a - self.prev)
            wgt = np.where(diff > 0.3, 1.0, base).astype(np.float32)   # real motion → follow immediately
            a = self.prev + (a - self.prev) * wgt
        self.prev = a
        return a


def opts_args(argv):
    """Pull the optional --opts <file> / --t <seconds> flags out of argv. → (positional args, opts file, seconds)."""
    pos, opts, tsec, i = [], None, None, 0
    while i < len(argv):
        if argv[i] == "--opts" and i + 1 < len(argv):
            opts = argv[i + 1]; i += 2
        elif argv[i] == "--t" and i + 1 < len(argv):
            try:
                tsec = float(argv[i + 1])
            except ValueError:
                pass
            i += 2
        else:
            pos.append(argv[i]); i += 1
    return pos, opts, tsec

# ── Choose WHO stays (person selection) ─────────────────────────────────────
class PersonSelector:
    """Keeps only the chosen people from the matte. People come from a face_blur.py analysis (face track per
    person); every foreground pixel is given to the person whose body (a capsule hanging below the face) it is
    closest to. Foreground that belongs to nobody the user chose is dropped → it gets the new background."""

    def __init__(self, analysis_path, keep_ids):
        import json, bisect
        self.bisect = bisect
        with open(analysis_path, encoding="utf-8") as fh:
            d = json.load(fh)
        self.keep = set(int(i) for i in keep_ids)
        self.tracks = []
        for t in d["tracks"]:
            boxes = {int(k): v for k, v in t["boxes"].items()}
            if boxes:
                self.tracks.append((int(t["id"]), boxes, sorted(boxes)))
        self.vw, self.vh = d.get("width", 1), d.get("height", 1)

    def _box(self, boxes, frames, f, hold=24):
        b = boxes.get(f)
        if b is not None:
            return b
        i = self.bisect.bisect_left(frames, f)
        c = [frames[j] for j in (i - 1, i) if 0 <= j < len(frames)]
        if not c:
            return None
        k = min(c, key=lambda q: abs(q - f))
        return boxes[k] if abs(k - f) <= hold else None        # carried for ~1 s while the face is not seen

    def mask(self, alpha, frame_idx, W, H):
        """→ float32 mask (alpha's size): 1 where a CHOSEN person is, 0 elsewhere."""
        h, w = alpha.shape
        sc = 320.0 / max(h, w)
        sh, sw = max(2, int(h * sc)), max(2, int(w * sc))
        a = cv2.resize(alpha, (sw, sh), interpolation=cv2.INTER_AREA)
        fx, fy = sw / float(W), sh / float(H)
        yy, xx = np.mgrid[0:sh, 0:sw].astype(np.float32)
        best = np.full((sh, sw), 1e9, np.float32)
        owner = np.full((sh, sw), -1, np.int32)
        present = False
        for pid, boxes, frames in self.tracks:
            b = self._box(boxes, frames, frame_idx)
            if b is None:
                continue
            present = True
            x, y, bw, bh = b[0] * fx, b[1] * fy, b[2] * fx, b[3] * fy
            cx, cy = x + bw / 2, y + bh / 2
            # body = a segment from the face centre down; its "width" is about two face widths
            ey = cy + bh * 3.0
            vx, vy = 0.0, ey - cy
            L2 = vy * vy + 1e-6
            t = np.clip(((xx - cx) * vx + (yy - cy) * vy) / L2, 0, 1)
            px, py = cx + t * vx, cy + t * vy
            width = bw * (0.9 + 0.55 * t)                    # shoulders are wider than the head
            dist = np.sqrt((xx - px) ** 2 + (yy - py) ** 2) / np.maximum(width, 1.0)
            upd = dist < best
            best = np.where(upd, dist, best)
            owner = np.where(upd, pid, owner)
        fg = a > 0.15
        n, lab = cv2.connectedComponents(fg.astype(np.uint8), connectivity=8)
        keep = np.zeros((sh, sw), np.float32)
        if present:
            seeded = np.zeros(n + 1, bool)                   # components that contain a confident part of somebody
            conf = fg & (best < 1.1)
            seeded[np.unique(lab[conf])] = True
            ok = fg & seeded[lab]
            kept_ids = np.isin(owner, list(self.keep))
            keep = (ok & kept_ids).astype(np.float32)
        keep = cv2.GaussianBlur(keep, (0, 0), 1.2)
        return cv2.resize(keep, (w, h), interpolation=cv2.INTER_LINEAR)

# ── Background source + compositing ────────────────────────────────────────
class Background:
    def __init__(self, mode, param, width, height):
        self.mode, self.param = mode, param
        self.w, self.h = width, height
        self.static = None
        self.cap = None
        self.blur = 35

        if mode == "color":
            hex_c = param.lstrip('#')
            try:
                r = int(hex_c[0:2], 16); g = int(hex_c[2:4], 16); b = int(hex_c[4:6], 16)
            except Exception:
                r, g, b = (21, 26, 31)
            self.static = np.full((height, width, 3), (b, g, r), dtype=np.uint8)

        elif mode == "image":
            img = imread_unicode(param)
            if img is not None:
                self.static = cv2.resize(img, (width, height))
            else:
                print(f"[STATUS] სურათი ვერ ჩაიტვირთა: {param} — blur-ზე გადასვლა.", flush=True)
                self.mode = "blur"

        elif mode == "video":
            if os.path.exists(param):
                self.cap = cv2.VideoCapture(param)
            if self.cap is None or not self.cap.isOpened():
                print(f"[STATUS] ვიდეო ვერ ჩაიტვირთა: {param} — blur-ზე გადასვლა.", flush=True)
                self.cap = None
                self.mode = "blur"

        if self.mode == "blur":
            try:
                s = max(5, int(float(param)))
            except Exception:
                s = 35
            self.blur = s + 1 if s % 2 == 0 else s

    def frame_for(self, frame):
        if self.mode == "blur":
            return self._blur(frame, self.blur)
        if self.mode == "video" and self.cap is not None:
            ok, bg = self.cap.read()
            if not ok:
                self.cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                ok, bg = self.cap.read()
            if ok:
                return cv2.resize(bg, (self.w, self.h))
            return self._blur(frame, 35)
        if self.static is not None:
            return self.static
        return self._blur(frame, 35)

    @staticmethod
    def _blur(frame, k):
        # blur at half size (4x cheaper), then scale back up — indistinguishable for a background
        h, w = frame.shape[:2]
        small = cv2.resize(frame, (max(2, w // 2), max(2, h // 2)), interpolation=cv2.INTER_AREA)
        kk = max(3, (k // 2) | 1)
        small = cv2.GaussianBlur(small, (kk, kk), 0)
        return cv2.resize(small, (w, h), interpolation=cv2.INTER_LINEAR)

    def close(self):
        if self.cap is not None:
            self.cap.release()


def compose(frame, alpha, bg_frame, invert):
    """alpha: float32 HxW (1 = person). Fast SIMD blend in uint8."""
    if invert:
        alpha = 1.0 - alpha
    a = np.ascontiguousarray(alpha, dtype=np.float32)
    return cv2.blendLinear(frame, bg_frame, a, 1.0 - a)


def _prefetch(cap, q):
    while True:
        ok, fr = cap.read()
        q.put(fr if ok else None)
        if not ok:
            return


# ── Modes ───────────────────────────────────────────────────────────────────
def run_process(input_path, mode, param, output_path, invert, opts=None):
    if not os.path.exists(input_path):
        print(f"[STATUS] ფაილი ვერ მოიძებნა: {input_path}", flush=True)
        sys.exit(1)

    cap = cv2.VideoCapture(input_path)
    if not cap.isOpened():
        print("[STATUS] ვიდეოს გახსნა ვერ მოხერხდა.", flush=True)
        sys.exit(1)

    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    opts = opts or dict(DEFAULT_OPTS)
    qw, qi = QUALITY.get(opts.get("quality"), QUALITY["balanced"])
    try:
        matte = get_matte(qw, qi)
    except (ImportError, RuntimeError) as e:
        print(f"[STATUS] {e}", flush=True)
        sys.exit(1)

    refiner = Refiner(opts)
    sel = make_selector(opts)
    off = [(float(a), float(b)) for a, b in opts.get("off", [])]
    bg = Background(mode, param, width, height)
    print("[STATUS] AI ფონის ანალიზი იწყება...", flush=True)

    # raw frames → ffmpeg (one encode only, audio copied from the source)
    ew, eh = width - width % 2, height - height % 2
    cmd = [find_ffmpeg(), "-y", "-loglevel", "error",
           "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{width}x{height}", "-r", f"{fps:.4f}", "-i", "-",
           "-i", input_path,
           "-map", "0:v:0", "-map", "1:a:0?",
           "-vf", f"crop={ew}:{eh}:0:0",
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output_path]
    enc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE)

    q = queue.Queue(maxsize=8)
    threading.Thread(target=_prefetch, args=(cap, q), daemon=True).start()

    frame_count = 0
    broken = False
    while True:
        frame = q.get()
        if frame is None:
            break
        frame_count += 1
        t_now = (frame_count - 1) / fps
        if off and any(a <= t_now <= b for a, b in off):          # the user keeps the original here
            matte.reset(); refiner.reset()
            out = frame
        else:
            a = matte.alpha(frame)
            if sel is not None:
                a = a * sel.mask(a, frame_count - 1, width, height)
            if matte.cut:
                refiner.reset()
            a = refiner.alpha(frame, a)
            out = compose(defringe(frame, a, float(opts.get("defringe", 0))), a, bg.frame_for(frame), invert)
        try:
            enc.stdin.write(out.tobytes())
        except (BrokenPipeError, OSError):
            broken = True
            break
        if frame_count % 5 == 0:
            print(f"[PROGRESS] {min(98, int(frame_count / max(total, 1) * 100))}", flush=True)

    try:
        enc.stdin.close()
    except Exception:
        pass
    err = enc.stderr.read().decode("utf-8", "ignore").strip()
    enc.wait()
    cap.release()
    bg.close()
    matte.close()

    if not broken and enc.returncode == 0 and os.path.exists(output_path):
        print("[PROGRESS] 100", flush=True)
        print("[SUCCESS] ვირტუალური ფონი დასრულდა!", flush=True)
    else:
        print(f"[STATUS] შეცდომა — გამოსავალი ვერ შეიქმნა. {err[-300:]}", flush=True)
        sys.exit(1)


def run_preview(input_path, mode, param, out_path, invert, opts=None, tsec=None):
    """One processed frame from the middle of the video → JPEG (so the user sees the look before rendering)."""
    cap = cv2.VideoCapture(input_path)
    if not cap.isOpened():
        print("[STATUS] ვიდეოს გახსნა ვერ მოხერხდა.", flush=True)
        sys.exit(1)
    opts = opts or dict(DEFAULT_OPTS)
    qw, qi = QUALITY.get(opts.get("quality"), QUALITY["balanced"])
    try:
        matte = get_matte(max(qw, 1280) if opts.get("quality") == "max" else 1280 if opts.get("quality") != "fast" else qw, qi)
    except (ImportError, RuntimeError) as e:
        print(f"[STATUS] {e}", flush=True)
        sys.exit(1)

    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fps_p = cap.get(cv2.CAP_PROP_FPS) or 30.0
    mid = total // 2 if total > 2 else 0
    if tsec is not None:
        mid = int(min(max(0, round(tsec * fps_p)), max(0, total - 1)))
    start = max(0, mid - 6)               # let the matting model "warm up" on a few frames before the shown one
    refiner = Refiner(opts)
    sel = make_selector(opts)
    vw = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); vh = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    cap.set(cv2.CAP_PROP_POS_FRAMES, start)
    frame, alpha = None, None
    for step in range(mid - start + 1):
        ok, fr = cap.read()
        if not ok:
            break
        frame = fr
        alpha = matte.alpha(fr)
        if sel is not None:
            alpha = alpha * sel.mask(alpha, start + step, vw, vh)
        if matte.cut:
            refiner.reset()
        alpha = refiner.alpha(fr, alpha)
    cap.release()
    if frame is None:
        print("[STATUS] კადრი ვერ წაიკითხა.", flush=True)
        sys.exit(1)

    h, w = frame.shape[:2]
    bg = Background(mode, param, w, h)
    t_shown = mid / fps_p
    if any(float(a) <= t_shown <= float(b) for a, b in opts.get("off", [])):
        out = frame                                       # the user keeps the original at this moment
    else:
        out = compose(defringe(frame, alpha, float(opts.get("defringe", 0))), alpha, bg.frame_for(frame), invert)
    bg.close()
    matte.close()
    imwrite_unicode(out_path, out)
    print("[SUCCESS] preview", flush=True)


def run_webcam(mode, param, invert):
    cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
    if not cap.isOpened():
        cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        print("[STATUS] კამერა ვერ მოიძებნა.", flush=True)
        sys.exit(1)
    try:
        matte = get_matte(640)
    except (ImportError, RuntimeError) as e:
        print(f"[STATUS] {e}", flush=True)
        sys.exit(1)

    ok, frame = cap.read()
    if not ok:
        print("[STATUS] კამერის კადრი ვერ წაიკითხა.", flush=True)
        sys.exit(1)
    h, w = frame.shape[:2]
    bg = Background(mode, param, w, h)

    win = "1Flow - Live Preview (ESC)"
    cv2.namedWindow(win, cv2.WINDOW_NORMAL)
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame = cv2.flip(frame, 1)                      # mirror, like a video call
        cv2.imshow(win, compose(frame, matte.alpha(frame), bg.frame_for(frame), invert))
        if (cv2.waitKey(1) & 0xFF) == 27:
            break
        if cv2.getWindowProperty(win, cv2.WND_PROP_VISIBLE) < 1:
            break

    cap.release()
    bg.close()
    matte.close()
    cv2.destroyAllWindows()

def main():
    a, opts_file, tsec = opts_args(sys.argv)
    opts = load_opts(opts_file)
    if len(a) >= 2 and a[1] == "preview" and len(a) >= 6:
        run_preview(a[2], a[3], a[4], a[5], len(a) > 6 and a[6] == "1", opts, tsec)
    elif len(a) >= 2 and a[1] == "webcam" and len(a) >= 4:
        run_webcam(a[2], a[3], len(a) > 5 and a[5] == "1")
    elif len(a) >= 5:
        run_process(a[1], a[2], a[3], a[4], len(a) > 5 and a[5] == "1", opts)
    else:
        print("[STATUS] გამოყენება: virtual_bg.py <input> <mode> <param> <output> [invert] [--opts file.json]", flush=True)
        sys.exit(1)

if __name__ == "__main__":
    main()
