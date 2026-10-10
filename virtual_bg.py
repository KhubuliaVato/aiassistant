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

    def __init__(self, session, width):
        self.sess, self.width = session, width
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
        if self._prev_small is not None and np.abs(g - self._prev_small).mean() > 45:
            self.reset()
        self._prev_small = g
        x = cv2.cvtColor(small, cv2.COLOR_BGR2RGB).astype(np.float32).transpose(2, 0, 1)[None] / 255.0
        ratio = np.array([min(1.0, 480.0 / W)], np.float32)
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


def get_matte(width=960):
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
        return RvmMatte(sess, width)
    except Exception as e:
        print(f"[STATUS] მაღალი ხარისხის მოდელი მიუწვდომელია ({type(e).__name__}) — სტანდარტული რეჟიმი.", flush=True)
        seg, close = get_segmenter()
        return MpMatte(seg, close)

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
def run_process(input_path, mode, param, output_path, invert):
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

    try:
        matte = get_matte(960)
    except (ImportError, RuntimeError) as e:
        print(f"[STATUS] {e}", flush=True)
        sys.exit(1)

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
        out = compose(frame, matte.alpha(frame), bg.frame_for(frame), invert)
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


def run_preview(input_path, mode, param, out_path, invert):
    """One processed frame from the middle of the video → JPEG (so the user sees the look before rendering)."""
    cap = cv2.VideoCapture(input_path)
    if not cap.isOpened():
        print("[STATUS] ვიდეოს გახსნა ვერ მოხერხდა.", flush=True)
        sys.exit(1)
    try:
        matte = get_matte(1280)
    except (ImportError, RuntimeError) as e:
        print(f"[STATUS] {e}", flush=True)
        sys.exit(1)

    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    mid = total // 2 if total > 2 else 0
    start = max(0, mid - 6)               # let the matting model "warm up" on a few frames before the shown one
    cap.set(cv2.CAP_PROP_POS_FRAMES, start)
    frame, alpha = None, None
    for _ in range(mid - start + 1):
        ok, fr = cap.read()
        if not ok:
            break
        frame = fr
        alpha = matte.alpha(fr)
    cap.release()
    if frame is None:
        print("[STATUS] კადრი ვერ წაიკითხა.", flush=True)
        sys.exit(1)

    h, w = frame.shape[:2]
    bg = Background(mode, param, w, h)
    out = compose(frame, alpha, bg.frame_for(frame), invert)
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
    a = sys.argv
    if len(a) >= 2 and a[1] == "preview" and len(a) >= 6:
        run_preview(a[2], a[3], a[4], a[5], len(a) > 6 and a[6] == "1")
    elif len(a) >= 2 and a[1] == "webcam" and len(a) >= 4:
        run_webcam(a[2], a[3], len(a) > 5 and a[5] == "1")
    elif len(a) >= 5:
        run_process(a[1], a[2], a[3], a[4], len(a) > 5 and a[5] == "1")
    else:
        print("[STATUS] გამოყენება: virtual_bg.py <input> <mode> <param> <output> [invert]", flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
