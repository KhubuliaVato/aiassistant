"""
face_blur.py — find faces in a video, track them as "people", and blur / pixelate / cover chosen ones.

    python face_blur.py analyze <input.mp4> <analysis.json>
        → detects + tracks every face, writes <analysis.json> and a thumbnail per person
          (<analysis>.p<ID>.jpg).

    python face_blur.py preview <input.mp4> <analysis.json> <frame_index> '<options json>' <out.jpg>
        → one processed frame.

    python face_blur.py process <input.mp4> <analysis.json> <output.mp4> '<options json>'
        → whole video, audio copied from the source.

Options JSON: {"style": "blur|pixel|black", "strength": 1-100, "pad": 0-100 (percent),
               "shape": "ellipse|rect", "ids": [1,2] | null (null = everyone)}
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
                                                   self._size, 0.6, 0.3, 5000)
        except Exception as e:
            status(f"YuNet მიუწვდომელია ({type(e).__name__}) — სტანდარტული დეტექტორი.")
            self.yunet = None
            self.haar = cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml")

    def detect(self, frame):
        """→ list of (x, y, w, h, score) in full-frame pixels."""
        h, w = frame.shape[:2]
        scale = min(1.0, DETECT_WIDTH / float(w))
        small = cv2.resize(frame, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA) if scale < 1 else frame
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
    def __init__(self, tid, frame, box, score):
        self.id = tid
        self.boxes = {frame: box}
        self.last_frame = frame
        self.last_box = box
        self.best = score

    def add(self, frame, box, score):
        self.boxes[frame] = box
        self.last_frame = frame
        self.last_box = box
        self.best = max(self.best, score)


def link(tracks_active, tracks_done, frame, dets, next_id, max_gap):
    """Greedy matching of this frame's detections to live tracks (IoU, then centre distance)."""
    used = set()
    pairs = []
    for t in tracks_active:
        for di, d in enumerate(dets):
            ov = iou(t.last_box, d[:4])
            cx_t, cy_t = t.last_box[0] + t.last_box[2] / 2, t.last_box[1] + t.last_box[3] / 2
            cx_d, cy_d = d[0] + d[2] / 2, d[1] + d[3] / 2
            dist = ((cx_t - cx_d) ** 2 + (cy_t - cy_d) ** 2) ** 0.5
            size = max(t.last_box[2], t.last_box[3], d[2], d[3])
            gap = frame - t.last_frame
            if ov > 0.15 or dist < size * (0.55 + 0.05 * min(gap, 8)):
                pairs.append((ov * 2 - dist / (size + 1e-6), t, di))
    pairs.sort(key=lambda p: -p[0])
    taken_t = set()
    for _, t, di in pairs:
        if id(t) in taken_t or di in used:
            continue
        t.add(frame, tuple(dets[di][:4]), dets[di][4])
        taken_t.add(id(t)); used.add(di)
    for di, d in enumerate(dets):
        if di in used:
            continue
        tracks_active.append(Track(next_id[0], frame, tuple(d[:4]), d[4]))
        next_id[0] += 1
    # retire tracks that haven't been seen for a while
    still = []
    for t in tracks_active:
        if frame - t.last_frame > max_gap:
            tracks_done.append(t)
        else:
            still.append(t)
    tracks_active[:] = still


def finalize(track, total_frames, pre=4, post=10, max_fill=45):
    """Fill short gaps by interpolation, extend a little before/after, smooth the boxes."""
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
    for k in range(1, pre + 1):               # face appears: cover it slightly early
        if first - k >= 0:
            full[first - k] = track.boxes[first]
    for k in range(1, post + 1):              # face is lost: keep covering for a moment (profile turn, blink of detection)
        if last + k < total_frames:
            full[last + k] = track.boxes[last]
    ks = sorted(full)
    arr = np.array([full[k] for k in ks], dtype=np.float32)
    if len(arr) >= 5:                          # moving-average smoothing (reflect at ends)
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
    x0 = int(max(0, x - padx)); y0 = int(max(0, y - pady))
    x1 = int(min(w, x + bw + padx)); y1 = int(min(h, y + bh + pady))
    if x1 - x0 < 4 or y1 - y0 < 4:
        return
    roi = frame[y0:y1, x0:x1]
    rh, rw = roi.shape[:2]
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

    if shape == "rect":
        roi[:] = eff
        return
    mask = np.zeros((rh, rw), np.float32)
    cv2.ellipse(mask, (rw // 2, rh // 2), (max(1, int(rw * 0.5) - 1), max(1, int(rh * 0.5) - 1)), 0, 0, 360, 1.0, -1)
    f = odd(min(rw, rh) * 0.12)
    mask = cv2.GaussianBlur(mask, (f, f), 0)[..., None]
    roi[:] = (eff.astype(np.float32) * mask + roi.astype(np.float32) * (1 - mask)).astype(np.uint8)


def boxes_at(analysis, frame_idx, ids):
    out = []
    key = str(frame_idx)
    for t in analysis["tracks"]:
        if ids is not None and t["id"] not in ids:
            continue
        b = t["boxes"].get(key)
        if b:
            out.append(b)
    return out


def process_frame(frame, analysis, frame_idx, opt):
    ids = opt.get("ids")
    ids = set(int(i) for i in ids) if ids is not None else None
    for b in boxes_at(analysis, frame_idx, ids):
        apply_effect(frame, b, opt.get("style", "blur"), int(opt.get("strength", 60)),
                     float(opt.get("pad", 30)), opt.get("shape", "ellipse"))
    return frame


# ─────────────────────────── commands ───────────────────────────
def cmd_analyze(inp, out_json):
    cap = cv2.VideoCapture(inp)
    if not cap.isOpened():
        status("ვიდეოს გახსნა ვერ მოხერხდა.")
        sys.exit(1)
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    det = Detector()
    status("სახეების ძებნა…")

    active, done = [], []
    next_id = [1]
    max_gap = int(max(8, fps * 0.6))
    n = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        dets = [d for d in det.detect(frame) if d[4] >= 0.6]
        link(active, done, n, dets, next_id, max_gap)
        n += 1
        if n % 10 == 0:
            print(f"[PROGRESS] {min(95, int(n / max(total, 1) * 95))}", flush=True)
    cap.release()
    done.extend(active)
    total = max(total, n)

    # blips (very short, low-confidence) are almost always false positives
    kept = [t for t in done if len(t.boxes) >= 5 or t.best >= 0.88]
    kept.sort(key=lambda t: min(t.boxes))
    people = []
    for new_id, t in enumerate(kept, 1):
        boxes = finalize(t, total)
        people.append({
            "id": new_id,
            "first": min(boxes), "last": max(boxes), "count": len(t.boxes),
            "best_frame": max(t.boxes, key=lambda f: t.boxes[f][2] * t.boxes[f][3]),
            "boxes": {str(k): v for k, v in boxes.items()},
        })

    # thumbnails (a crop of the biggest, clearest appearance of each person)
    cap = cv2.VideoCapture(inp)
    base = out_json
    for p in people:
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

    data = {"fps": fps, "width": width, "height": height, "frames": total, "tracks": people}
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
            cmd_analyze(a[2], a[3])
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
