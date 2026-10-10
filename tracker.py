import sys
import os
import subprocess
import cv2
import numpy as np
import re

# ═══════════════════════════════════════════════════════════════════════
# HELPERS
# ═══════════════════════════════════════════════════════════════════════
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
    for p in [os.path.join(script_dir,"ffmpeg.exe"), os.path.abspath("ffmpeg.exe"),
               os.path.abspath(os.path.join(script_dir,"..","..","..","ffmpeg.exe"))]:
        if os.path.exists(p): return p
    return "ffmpeg"

def get_video_duration(ffmpeg_bin, video_path):
    try:
        r = subprocess.run([ffmpeg_bin,'-i',video_path], capture_output=True,
                           text=True, encoding='utf-8', errors='ignore')
        m = re.search(r"Duration:\s+(\d+):(\d+):(\d+)", r.stderr)
        if m: return int(m.group(1))*3600+int(m.group(2))*60+int(m.group(3))
    except: pass
    return 4.0

def get_video_dimensions(ffmpeg_bin, video_path):
    r = subprocess.run([ffmpeg_bin,'-i',video_path], capture_output=True,
                       text=True, encoding='utf-8', errors='ignore')
    m = re.search(r'(\d{2,5})x(\d{2,5})', r.stderr)
    return (int(m.group(1)), int(m.group(2))) if m else (None, None)

def find_font():
    for p in [r"C:\Windows\Fonts\sylfaen.ttf", r"C:\Windows\Fonts\arial.ttf",
               r"C:\Windows\Fonts\times.ttf",   r"C:\Windows\Fonts\calibri.ttf"]:
        if os.path.exists(p): return p
    return None

# ═══════════════════════════════════════════════════════════════════════
# THUMBNAILS
# ═══════════════════════════════════════════════════════════════════════
def generate_thumbnails(folder_path, cache_dir=None):
    if not os.path.exists(folder_path): return
    if not cache_dir:
        cache_dir = os.path.join(folder_path, ".flow_cache")
    os.makedirs(cache_dir, exist_ok=True)
    ffmpeg_bin = find_ffmpeg()
    for file in os.listdir(folder_path):
        ext = os.path.splitext(file)[1].lower()
        if ext in ['.mp4','.mov','.mxf']:
            video_path = os.path.join(folder_path, file)
            thumb_path = os.path.join(cache_dir, file+".jpg")
            if os.path.exists(thumb_path): continue
            mid = get_video_duration(ffmpeg_bin, video_path) / 2.0
            subprocess.run([ffmpeg_bin,'-y','-loglevel','quiet',
                            '-ss',str(mid),'-i',video_path,
                            '-vframes','1','-s','240x135',thumb_path])

# ═══════════════════════════════════════════════════════════════════════
# BUILT-IN LOWER THIRD TEMPLATES
# ═══════════════════════════════════════════════════════════════════════
TEMPLATES = {
    "1tv":     {"box":(255,255,255, 88),"accent":(255,255,255,220),"border":(255,255,255,180),"name_clr":(255,255,255,235),"desc_clr":(255,255,255,190),"shadow":(0,0,0,110)},
    "dark":    {"box":( 12, 22, 52,220),"accent":( 20,100,230,255),"border":( 20,100,230,160),"name_clr":(255,255,255,245),"desc_clr":(180,210,255,210),"shadow":(0,0,0,130)},
    "red":     {"box":(175, 18, 18,215),"accent":(255,255,255,255),"border":(255,255,255, 80),"name_clr":(255,255,255,245),"desc_clr":(255,220,220,205),"shadow":(0,0,0,130)},
    "minimal": {"box":(  0,  0,  0, 45),"accent":(220,175, 30,255),"border":(220,175, 30,200),"name_clr":(255,255,255,245),"desc_clr":(220,210,180,200),"shadow":(0,0,0,120)},
}

def make_builtin_overlay(vid_w, vid_h, lt_name, lt_desc, tmpl_id, font_path):
    from PIL import Image, ImageDraw, ImageFont
    T = TEMPLATES.get(tmpl_id, TEMPLATES["1tv"])
    img  = Image.new("RGBA",(vid_w,vid_h),(0,0,0,0))
    draw = ImageDraw.Draw(img)
    box_y=int(vid_h*0.718); box_h=int(vid_h*0.148)
    border=max(1,int(vid_h*0.0022)); acc_w=max(4,int(vid_w*0.016))
    tx=int(vid_w*0.060); name_y=box_y+int(box_h*0.12); desc_y=box_y+int(box_h*0.56)
    draw.rectangle([0,box_y,vid_w,box_y+box_h], fill=T["box"])
    draw.rectangle([0,box_y,vid_w,box_y+border], fill=T["border"])
    draw.rectangle([0,box_y+box_h-border,vid_w,box_y+box_h], fill=T["border"])
    draw.rectangle([0,box_y,acc_w,box_y+box_h], fill=T["accent"])
    try:
        nf=ImageFont.truetype(font_path, max(14,int(vid_h*0.037)))
        df=ImageFont.truetype(font_path, max(11,int(vid_h*0.023)))
    except: nf=df=ImageFont.load_default()
    def dt(pos, txt, font, clr):
        draw.text((pos[0]+1,pos[1]+1), txt, font=font, fill=(*T["shadow"][:3],T["shadow"][3]))
        draw.text(pos, txt, font=font, fill=clr)
    if lt_name and lt_name.strip(): dt((tx,name_y), lt_name.strip(), nf, T["name_clr"])
    if lt_desc and lt_desc.strip():  dt((tx,desc_y), lt_desc.strip(), df, T["desc_clr"])
    return np.array(img)

def make_custom_overlay(vid_w, vid_h, overlay_path, lt_name, lt_desc, font_path):
    from PIL import Image, ImageDraw, ImageFont
    try: src = Image.open(overlay_path).convert("RGBA").resize((vid_w,vid_h), Image.LANCZOS)
    except: return make_builtin_overlay(vid_w, vid_h, lt_name, lt_desc, "1tv", font_path)
    img  = src.copy(); draw = ImageDraw.Draw(img)
    if (lt_name and lt_name.strip()) or (lt_desc and lt_desc.strip()):
        box_y=int(vid_h*0.718); box_h=int(vid_h*0.148)
        tx=int(vid_w*0.075); name_y=box_y+int(box_h*0.12); desc_y=box_y+int(box_h*0.56)
        try: nf=ImageFont.truetype(font_path,max(14,int(vid_h*0.037))); df=ImageFont.truetype(font_path,max(11,int(vid_h*0.023)))
        except: nf=df=ImageFont.load_default()
        def dt(pos,txt,font):
            draw.text((pos[0]+1,pos[1]+1),txt,font=font,fill=(0,0,0,120))
            draw.text(pos,txt,font=font,fill=(255,255,255,235))
        if lt_name and lt_name.strip(): dt((tx,name_y),lt_name.strip(),nf)
        if lt_desc and lt_desc.strip():  dt((tx,desc_y),lt_desc.strip(),df)
    return np.array(img)

# ═══════════════════════════════════════════════════════════════════════
# WATERMARK / LOGO OVERLAY
# ═══════════════════════════════════════════════════════════════════════
def make_watermark_overlay(vid_w, vid_h, wm_path, position, wm_size, opacity):
    from PIL import Image
    try: wm = Image.open(wm_path).convert("RGBA")
    except Exception as e:
        print(f"[STATUS] Watermark ვერ ჩაიტვირთა: {e}", flush=True); return None
    new_w = max(10, int(vid_w * wm_size))
    new_h = int(wm.height * (new_w / wm.width))
    wm = wm.resize((new_w, new_h), Image.LANCZOS)
    r,g,b,a = wm.split()
    a = a.point(lambda x: int(x * opacity))
    wm.putalpha(a)
    pad = max(8, int(vid_w * 0.025))
    pos_map = {
        "topleft":     (pad, pad),
        "topright":    (vid_w - new_w - pad, pad),
        "bottomleft":  (pad, vid_h - new_h - pad),
        "bottomright": (vid_w - new_w - pad, vid_h - new_h - pad),
    }
    x, y = pos_map.get(position, pos_map["topright"])
    canvas = Image.new("RGBA", (vid_w, vid_h), (0,0,0,0))
    canvas.paste(wm, (x, y), wm)
    return np.array(canvas)

# ═══════════════════════════════════════════════════════════════════════
# COMPOSITE OVERLAYS — lower third + watermark in ONE frame pass
# ═══════════════════════════════════════════════════════════════════════
def composite_overlays(ffmpeg_bin, base_video, output_path,
                       lt_name, lt_desc, tmpl_id, lt_duration, lt_overlay_path,
                       wm_path, wm_position, wm_size, wm_opacity,
                       preview_sec=0):
    try: from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        print("[STATUS] Pillow არ არის: pip install Pillow", flush=True)
        os.replace(base_video, output_path); return

    font_path = find_font()
    cap   = cv2.VideoCapture(base_video)
    vid_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    vid_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps   = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    # ── Build overlay images ──────────────────────────────────────────
    has_lt = bool(lt_name and lt_name.strip()) or (tmpl_id=='custom' and lt_overlay_path)
    if has_lt:
        if tmpl_id=='custom' and lt_overlay_path and os.path.exists(lt_overlay_path):
            lt_ov = make_custom_overlay(vid_w, vid_h, lt_overlay_path, lt_name, lt_desc, font_path)
        elif font_path:
            lt_ov = make_builtin_overlay(vid_w, vid_h, lt_name, lt_desc, tmpl_id, font_path)
        else:
            lt_ov = None
    else:
        lt_ov = None

    wm_ov = None
    if wm_path and os.path.exists(wm_path) and wm_size > 0:
        wm_ov = make_watermark_overlay(vid_w, vid_h, wm_path, wm_position, wm_size, wm_opacity)

    if lt_ov is None and wm_ov is None:
        os.replace(base_video, output_path); return

    print(f"[STATUS] Overlay-ები ემატება (LT={has_lt}, WM={wm_ov is not None})...", flush=True)

    # Pre-compute float arrays
    lt_bgr = lt_alpha = wm_bgr = wm_alpha = None
    if lt_ov is not None:
        lt_bgr   = lt_ov[:,:,:3][:,:,::-1].astype(np.float32)
        lt_alpha = lt_ov[:,:,3:4].astype(np.float32) / 255.0
    if wm_ov is not None:
        wm_bgr   = wm_ov[:,:,:3][:,:,::-1].astype(np.float32)
        wm_alpha = wm_ov[:,:,3:4].astype(np.float32) / 255.0

    ANIM_IN=0.55; ANIM_OUT=0.35

    cmd = [ffmpeg_bin,'-y','-loglevel','warning',
           '-f','rawvideo','-vcodec','rawvideo','-pix_fmt','bgr24',
           '-s',f'{vid_w}x{vid_h}','-r',str(fps),
           '-i','-','-i',base_video,'-map','0:v','-map','1:a?',
           '-c:v','libx264','-preset','fast','-pix_fmt','yuv420p',
           '-c:a','copy', output_path]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)

    max_frames = int(fps * preview_sec) if preview_sec > 0 else 0
    frame_num  = 0

    while cap.isOpened():
        ret, frame = cap.read()
        if not ret: break
        if max_frames > 0 and frame_num >= max_frames: break

        t = frame_num / fps

        # Lower third with wipe-in / wipe-out animation
        if lt_alpha is not None:
            if t < ANIM_IN:
                reveal = t / ANIM_IN
            elif lt_duration > 0 and t >= lt_duration:
                reveal = 0.0
            elif lt_duration > 0 and t > lt_duration - ANIM_OUT:
                reveal = max(0.0, (lt_duration - t) / ANIM_OUT)
            else:
                reveal = 1.0
            if reveal > 0:
                cols  = int(vid_w * reveal)
                alpha = lt_alpha.copy()
                if cols < vid_w: alpha[:, cols:, :] = 0
                f = frame.astype(np.float32)
                f = f*(1.0-alpha) + lt_bgr*alpha
                frame = np.clip(f,0,255).astype(np.uint8)

        # Watermark — always visible, no animation
        if wm_alpha is not None:
            f = frame.astype(np.float32)
            f = f*(1.0-wm_alpha) + wm_bgr*wm_alpha
            frame = np.clip(f,0,255).astype(np.uint8)

        try:    proc.stdin.write(frame.tobytes())
        except: break
        frame_num += 1

        if total > 0 and frame_num % 45 == 0:
            print(f"[STATUS] Overlay render: {int(frame_num/max(total,1)*100)}%", flush=True)

    cap.release()
    if proc.stdin: proc.stdin.close()
    proc.wait()
    try: os.remove(base_video)
    except: pass
    print("[STATUS] Overlay-ები დაემატა!", flush=True)

# ═══════════════════════════════════════════════════════════════════════
# MAIN PROCESSOR
# ═══════════════════════════════════════════════════════════════════════
def process_video(input_path, speed_mode, output_path,
                  lt_name='', lt_desc='', tmpl_id='1tv', lt_duration=0.0, lt_overlay_path='',
                  wm_path='', wm_position='topright', wm_size=0.15, wm_opacity=1.0,
                  preview_sec=0):
    ffmpeg_bin    = find_ffmpeg()
    temp_mp4_path = None
    original_in   = input_path

    use_overlay = (bool(lt_name and lt_name.strip()) or
                   (tmpl_id=='custom' and lt_overlay_path) or
                   (wm_path and os.path.exists(wm_path) and wm_size>0))
    render_out = (output_path+".base.mp4") if use_overlay else output_path

    if input_path.lower().endswith('.mxf'):
        print("[STATUS] MXF konvertacia...", flush=True)
        temp_mp4_path = input_path+".temp_convert.mp4"
        subprocess.run([ffmpeg_bin,'-y','-loglevel','warning','-i',input_path,
                        '-c:v','libx264','-preset','ultrafast','-crf','22',
                        '-c:a','aac','-b:a','192k',temp_mp4_path])
        input_path = temp_mp4_path

    cap = cv2.VideoCapture(input_path)
    if not cap.isOpened(): sys.exit(1)
    width=int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height=int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    fps=cap.get(cv2.CAP_PROP_FPS)
    if fps<=0: fps=30.0
    total_frames=int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    out_width=int(height*9/16)
    if out_width%2!=0: out_width+=1

    cmd_ffmpeg=[ffmpeg_bin,'-y','-loglevel','warning',
                '-f','rawvideo','-vcodec','rawvideo','-pix_fmt','bgr24',
                '-s',f'{out_width}x{height}','-r',str(fps),
                '-i','-','-i',original_in,'-map','0:v','-map','1:a?',
                '-c:v','libx264','-preset','ultrafast','-pix_fmt','yuv420p',
                '-c:a','aac','-b:a','192k',render_out]
    proc=subprocess.Popen(cmd_ffmpeg, stdin=subprocess.PIPE)
    face_cascade=cv2.CascadeClassifier(
        cv2.data.haarcascades+'haarcascade_frontalface_default.xml')

    current_x=(width-out_width)//2; desired_x=current_x; alpha=0.08; frame_count=0
    locked_cx=None; locked_area=None  # tracked subject's position + face size (original-res)
    POS_TOL=0.08   # fraction of frame width still considered "the same person"
    SIZE_TOL=0.35  # candidate face area must stay within ±35% of the locked size
    max_crop_frames = int(fps*preview_sec) if preview_sec>0 else 0

    if speed_mode=="accurate":   frame_skip,use_ds,ai_w=1,False,width
    elif speed_mode=="ultrafast": frame_skip,use_ds,ai_w=6,True,240
    else:                         frame_skip,use_ds,ai_w=3,True,400
    sf=ai_w/width

    print("[STATUS] AI analizi daiwyo...", flush=True)
    while cap.isOpened():
        ret,frame=cap.read()
        if not ret: break
        if max_crop_frames>0 and frame_count>=max_crop_frames: break
        frame_count+=1
        if total_frames>0 and frame_count%15==0:
            print(f"[PROGRESS] {min(99,int(frame_count/total_frames*100))}", flush=True)
        if frame_count%frame_skip==0 or frame_count==1:
            gray=cv2.cvtColor(frame,cv2.COLOR_BGR2GRAY)
            sg=cv2.resize(gray,(ai_w,int(height*sf))) if use_ds else gray
            min_face=max(30,int(ai_w*0.10))
            faces=face_cascade.detectMultiScale(sg,1.1,5,minSize=(min_face,min_face))
            if len(faces)>0:
                # Convert every detected face to an (x-center, area) pair in
                # original-resolution coordinates.
                candidates=[]
                for f in faces:
                    cx=f[0]+f[2]//2
                    ocx=int(cx/sf) if use_ds else int(cx)
                    candidates.append((ocx,f[2]*f[3]))

                if locked_cx is None:
                    # No subject locked yet -- anchor on the largest (closest) face.
                    orig_cx,orig_area=max(candidates,key=lambda c:c[1])
                    locked_cx=orig_cx; locked_area=orig_area
                    desired_x=orig_cx-out_width//2
                else:
                    # Only accept a candidate as "the same subject" if it's
                    # both close in position AND a similar face size to the
                    # one we're tracking. A passerby crossing near the main
                    # subject is positionally close but a different depth/
                    # size -- this is what let them steal the lock before.
                    qualifying=[c for c in candidates
                                if abs(c[0]-locked_cx)<=width*POS_TOL
                                and (1-SIZE_TOL)*locked_area<=c[1]<=(1+SIZE_TOL)*locked_area]
                    if qualifying:
                        orig_cx,orig_area=min(qualifying,key=lambda c:abs(c[0]-locked_cx))
                        locked_cx=orig_cx; locked_area=orig_area
                        desired_x=orig_cx-out_width//2
                    else:
                        # Nothing detected this cycle plausibly matches the
                        # tracked subject (briefly turned away, occluded by
                        # someone passing, etc). Hold the current target
                        # instead of jumping to whatever else was found.
                        best_cx,best_area=max(candidates,key=lambda c:c[1])
                        if abs(best_cx-locked_cx)>width*0.4:
                            # Nothing anywhere near the tracked subject --
                            # they've likely left frame; reacquire.
                            locked_cx=best_cx; locked_area=best_area
                            desired_x=best_cx-out_width//2
        desired_x=max(0,min(desired_x,width-out_width))
        current_x=int(alpha*desired_x+(1-alpha)*current_x)
        current_x=max(0,min(current_x,width-out_width))
        try: proc.stdin.write(frame[0:height,current_x:current_x+out_width].tobytes())
        except: break

    cap.release()
    if proc.stdin: proc.stdin.close()
    proc.wait()
    if temp_mp4_path and os.path.exists(temp_mp4_path):
        try: os.remove(temp_mp4_path)
        except: pass

    if use_overlay and os.path.exists(render_out):
        composite_overlays(ffmpeg_bin, render_out, output_path,
                           lt_name, lt_desc, tmpl_id, lt_duration, lt_overlay_path,
                           wm_path, wm_position, wm_size, wm_opacity, preview_sec)
    print("[SUCCESS] damuSaveba dasrulda!", flush=True)

# ═══════════════════════════════════════════════════════════════════════
# FACE-CENTRED 9:16 PREVIEW (single frame, same detector as process_video)
# ═══════════════════════════════════════════════════════════════════════
_FACE_CASCADE = None

def _face_cx(img):
    """x-centre (original resolution) of the largest face, or None. Same Haar cascade and
    the same relative minimum size as process_video's 'fast' mode."""
    global _FACE_CASCADE
    if _FACE_CASCADE is None:
        _FACE_CASCADE = cv2.CascadeClassifier(cv2.data.haarcascades+'haarcascade_frontalface_default.xml')
    h, w = img.shape[:2]
    ai_w = 400
    sf = ai_w/w
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    small = cv2.resize(gray, (ai_w, max(1, int(h*sf))))
    min_face = max(30, int(ai_w*0.10))
    faces = _FACE_CASCADE.detectMultiScale(small, 1.1, 5, minSize=(min_face, min_face))
    if len(faces) == 0: return None
    x, y, fw, fh = max(faces, key=lambda f: f[2]*f[3])
    return int((x + fw//2)/sf)

def _write_vertical_crop(img, cx, out_path):
    h, w = img.shape[:2]
    out_w = min(w, int(h*9/16))
    x0 = max(0, min(cx - out_w//2, w - out_w))
    # cv2.imwrite/imread can't handle non-ASCII paths on Windows (Georgian file names get mangled),
    # so encode in memory and let Python do the file I/O.
    ok, buf = cv2.imencode('.jpg', img[0:h, x0:x0+out_w], [cv2.IMWRITE_JPEG_QUALITY, 92])
    if not ok: sys.exit(1)
    with open(out_path, 'wb') as fh:
        fh.write(buf.tobytes())

def face_preview(image_path, out_path):
    """9:16 crop of one image centred on the largest face (centre crop if none).
    Prints '[FACE] found' or '[FACE] none'."""
    img = cv2.imdecode(np.fromfile(image_path, dtype=np.uint8), cv2.IMREAD_COLOR)
    if img is None: sys.exit(1)
    cx = _face_cx(img)
    print("[FACE] found" if cx is not None else "[FACE] none", flush=True)
    _write_vertical_crop(img, cx if cx is not None else img.shape[1]//2, out_path)

def face_preview_video(video_path, out_path):
    """Looks at several points of the video and uses the first frame that contains a face
    (a single mid-clip frame often has none: speaker turned away, b-roll, ...).
    Falls back to the centre crop of the first readable frame. Exit code 1 if unreadable."""
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened(): sys.exit(1)
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    first = None
    for frac in (0.5, 0.3, 0.7, 0.15, 0.85, 0.4, 0.6, 0.25, 0.75, 0.05):
        if total > 0: cap.set(cv2.CAP_PROP_POS_FRAMES, int(total*frac))
        ok, frame = cap.read()
        if not ok: continue
        if first is None: first = frame
        cx = _face_cx(frame)
        if cx is not None:
            print("[FACE] found", flush=True)
            _write_vertical_crop(frame, cx, out_path)
            cap.release(); return
    cap.release()
    if first is None: sys.exit(1)
    print("[FACE] none", flush=True)
    _write_vertical_crop(first, first.shape[1]//2, out_path)

# ═══════════════════════════════════════════════════════════════════════
# ENTRY POINT
# ═══════════════════════════════════════════════════════════════════════
if __name__=="__main__":
    if len(sys.argv)<3: sys.exit(1)
    if sys.argv[1]=="--facepreview" and len(sys.argv)>=4:
        face_preview(sys.argv[2], sys.argv[3]); sys.exit(0)
    if sys.argv[1]=="--facepreview-video" and len(sys.argv)>=4:
        face_preview_video(sys.argv[2], sys.argv[3]); sys.exit(0)
    if sys.argv[1]=="--thumbnails":
        cache_dir=sys.argv[3] if len(sys.argv)>3 else None
        generate_thumbnails(sys.argv[2],cache_dir); sys.exit(0)

    a=sys.argv
    video_file    = a[1]
    mode          = a[2]
    out_file      = a[3]
    lt_name       = a[4]            if len(a)>4  else ""
    lt_desc       = a[5]            if len(a)>5  else ""
    tmpl_id       = a[6]            if len(a)>6  else "1tv"
    lt_duration   = float(a[7])     if len(a)>7  else 0.0
    lt_overlay    = a[8]            if len(a)>8  else ""
    wm_path       = a[9]            if len(a)>9  else ""
    wm_position   = a[10]           if len(a)>10 else "topright"
    wm_size       = float(a[11])    if len(a)>11 else 0.15
    wm_opacity    = float(a[12])    if len(a)>12 else 1.0
    preview_sec   = int(a[13])      if len(a)>13 else 0

    process_video(video_file, mode, out_file,
                  lt_name, lt_desc, tmpl_id, lt_duration, lt_overlay,
                  wm_path, wm_position, wm_size, wm_opacity, preview_sec)
    sys.exit(0)