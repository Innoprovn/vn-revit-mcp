"""Quay video hướng dẫn tự động: chạy kịch bản thao tác thật trên Windows (chuột thật, có con trỏ), quay màn hình bằng ffmpeg,
ghi thời điểm từng bước rồi gắn phụ đề (mỗi bước đủ thời gian đọc) và vòng tròn đánh dấu chỗ bấm.

Dùng:  python recorder.py kich-ban.py ra.mp4
Kịch bản là file Python định nghĩa TITLE và hàm run(r) gọi các thao tác của Recorder (xem lessons/*.py).

Không tốn token khi quay: tọa độ bấm lấy từ UI Automation hoặc tính trước, không cần nhìn ảnh.
Cần: ffmpeg (winget install Gyan.FFmpeg), Windows 10/11. Không cần thư viện Python ngoài.
"""
import ctypes, ctypes.wintypes as W, glob, importlib.util, json, math, os, subprocess, sys, time

user32 = ctypes.windll.user32
user32.SetProcessDPIAware()

# Mọi tiến trình phụ (PowerShell cho OCR/UIA, ffmpeg) chạy không cửa sổ: cửa sổ console mới sẽ cướp focus,
# làm cú bấm kế tiếp vào Revit chỉ để kích hoạt cửa sổ.
_run, _popen_init = subprocess.run, subprocess.Popen.__init__
def _quiet_run(*a, **k):
    k.setdefault("creationflags", 0x08000000); return _run(*a, **k)
def _quiet_popen(self, *a, **k):
    k.setdefault("creationflags", 0x08000000); _popen_init(self, *a, **k)
subprocess.run = _quiet_run
subprocess.Popen.__init__ = _quiet_popen

FFMPEG = os.environ.get("FFMPEG") or (glob.glob(os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin\ffmpeg.exe")) or ["ffmpeg"])[0]
CPS = 14.0          # tốc độ đọc phụ đề (ký tự/giây)
MIN_CAPTION = 3.0   # phụ đề hiện ít nhất (giây)


def reading_time(text):
    return max(MIN_CAPTION, len(text) / CPS + 1.2)


# ---------- chuột, bàn phím (SendInput) ----------
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long), ("mouseData", W.DWORD), ("dwFlags", W.DWORD), ("time", W.DWORD), ("dwExtraInfo", ctypes.c_size_t)]
class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", W.WORD), ("wScan", W.WORD), ("dwFlags", W.DWORD), ("time", W.DWORD), ("dwExtraInfo", ctypes.c_size_t)]
class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
class INPUT(ctypes.Structure):
    _fields_ = [("type", W.DWORD), ("u", _U)]

def _send(inp): user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))

def cursor():
    p = W.POINT(); user32.GetCursorPos(ctypes.byref(p)); return p.x, p.y

def mouse_button(down, right=False):
    flag = (0x0008 if down else 0x0010) if right else (0x0002 if down else 0x0004)
    i = INPUT(type=0); i.u.mi = MOUSEINPUT(0, 0, 0, flag, 0, 0); _send(i)

def key(vk, up=False):
    i = INPUT(type=1); i.u.ki = KEYBDINPUT(vk, 0, 0x0002 if up else 0, 0, 0); _send(i)

def unicode_char(ch):
    for up in (False, True):
        i = INPUT(type=1); i.u.ki = KEYBDINPUT(0, ord(ch), 0x0004 | (0x0002 if up else 0), 0, 0); _send(i)


# ---------- UI Automation (qua PowerShell, trả tâm phần tử theo tên) ----------
UIA_PS = r"""
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = if ($env:UIA_HWND) { [Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$env:UIA_HWND) } else { [Windows.Automation.AutomationElement]::RootElement }
$cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $env:UIA_NAME)
$el = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
if ($el) { $r = $el.Current.BoundingRectangle; "{0},{1},{2},{3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height }
"""

UIA_ALL_PS = r"""
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$env:UIA_HWND)
foreach ($e in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
  try { if ($e.Current.Name -match $env:UIA_NAME) { $r = $e.Current.BoundingRectangle; if ($r.Width -gt 0) { "{0},{1},{2},{3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height } } } catch {}
}
"""

def find_all(pattern, hwnd):
    """Mọi phần tử có tên khớp regex: danh sách (x, y, w, h)."""
    env = dict(os.environ, UIA_NAME=pattern, UIA_HWND=str(hwnd))
    out = subprocess.run(["powershell", "-NoProfile", "-Command", UIA_ALL_PS], capture_output=True, text=True, env=env, encoding="utf-8").stdout
    return [tuple(map(int, l.split(","))) for l in out.split() if l.count(",") == 3]


def find_element(name, hwnd=None):
    env = dict(os.environ, UIA_NAME=name, UIA_HWND=str(hwnd or ""))
    out = subprocess.run(["powershell", "-NoProfile", "-Command", UIA_PS], capture_output=True, text=True, env=env).stdout.strip()
    if not out or "," not in out: return None
    x, y, w, h = map(int, out.splitlines()[-1].split(","))
    return (x + w // 2, y + h // 2) if w > 0 and h > 0 and x > -1000 else None


def revit_hwnd():
    out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process Revit | Sort-Object { $_.Path -like '*Revit 2026*' }, StartTime -Descending | Select-Object -First 1).MainWindowHandle"], capture_output=True, text=True).stdout.strip()
    return int(out)


def windows():
    """Cửa sổ đang hiện: danh sách (hwnd, tiêu đề, pid)."""
    found = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, W.HWND, W.LPARAM)
    def each(h, _):
        if user32.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(512); user32.GetWindowTextW(h, buf, 512)
            pid = W.DWORD(); user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
            if buf.value: found.append((h, buf.value, pid.value))
        return True
    user32.EnumWindows(each, 0)
    return found


def find_window(pattern, timeout=20):
    """Chờ cửa sổ có tiêu đề khớp regex, trả hwnd."""
    import re
    end = time.time() + timeout
    while time.time() < end:
        for h, title, _ in windows():
            if re.search(pattern, title): return h
        time.sleep(0.4)
    raise RuntimeError(f"Không thấy cửa sổ '{pattern}'")


def place(hwnd, region=(0, 0, 1920, 920)):
    """Đặt cửa sổ (không phóng to) vừa khít vùng quay."""
    user32.ShowWindow(hwnd, 9)
    x, y, w, h = region
    user32.MoveWindow(hwnd, x - 7, y, w + 14, h + 7, True)      # Windows 11: viền trong suốt 7 px trái/phải/dưới


def focus(hwnd):
    """Đưa cửa sổ lên trước (nhấn Alt giả để Windows cho phép đổi cửa sổ nền trước, rồi Esc tắt KeyTips của ribbon)."""
    if user32.GetForegroundWindow() != hwnd:
        key(0x12); key(0x12, up=True)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.3)
        key(0x1B); key(0x1B, up=True)
    time.sleep(0.6)


def element_rect(name, hwnd=None):
    env = dict(os.environ, UIA_NAME=name, UIA_HWND=str(hwnd or ""))
    out = subprocess.run(["powershell", "-NoProfile", "-Command", UIA_PS], capture_output=True, text=True, env=env).stdout.strip()
    if not out or "," not in out: return None
    return tuple(map(int, out.splitlines()[-1].split(",")))


# ---------- cầu nối vn-revit-mcp (named pipe) ----------
def revit(name, args=None, pid=None, method="invoke"):
    import pathlib
    if pid is None:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process Revit | Sort-Object { $_.Path -like '*Revit 2026*' }, StartTime -Descending | Select-Object -First 1).Id"], capture_output=True, text=True).stdout.strip()
        pid = int(out)
    payload = json.dumps({"id": "1", "method": method, "params": {"name": name, "arguments": args or {}} if method == "invoke" else None}, ensure_ascii=False)
    with open(rf"\\.\pipe\InnoPro.VnRevitMcp.{pid}", "r+b", buffering=0) as f:
        f.write((payload + "\n").encode("utf-8"))
        line = b""
        while not line.endswith(b"\n"):
            chunk = f.read(65536)
            if not chunk: break
            line += chunk
    return json.loads(line.decode("utf-8"))




HERE = os.path.dirname(os.path.abspath(__file__))


def screen_png(path, region):
    x, y, w, h = region
    ps = (f"Add-Type -AssemblyName System.Drawing; $b=New-Object Drawing.Bitmap {w},{h}; $g=[Drawing.Graphics]::FromImage($b); "
          f"$g.CopyFromScreen({x},{y},0,0,$b.Size); $b.Save('{path}')")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], check=True)


def ocr(path):
    """OCR có sẵn của Windows: danh sách (x, y, w, h, chữ) theo tọa độ ảnh."""
    out = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(HERE, "ocr.ps1"), path],
                         capture_output=True, text=True, encoding="utf-8").stdout
    words = []
    for line in out.splitlines():
        if "\t" not in line: continue
        box, text = line.split("\t", 1)
        words.append((*map(int, box.split(",")), text))
    return words


# Quay vùng 1920x920 phía trên; mọi cửa sổ được đặt gọn trong vùng này (place()). Phần màn hình bên dưới (taskbar,
# chữ "Activate Windows" của Windows chưa kích hoạt) nằm ngoài khung. Video đệm thêm dải đen 160 px cho phụ đề.
REGION = (0, 0, 1920, 920)
ALWAYS = []


class Recorder:
    def __init__(self, out, region=REGION, fps=24):
        self.out, self.region, self.fps = out, region, fps
        self.steps, self.clicks = [], []
        self.masks = [dict(m) for m in ALWAYS]
        self.segments = []          # (file, thời điểm thực lúc dừng, thời lượng)
        self.proc = None

    # ----- quay (nhiều đoạn; lúc tạm dừng không quay, ví dụ chờ Revit khởi động) -----
    def start(self):
        x, y, w, h = self.region
        self.seg_file = f"{self.out}.seg{len(self.segments)}.mp4"
        self.proc = subprocess.Popen([FFMPEG, "-y", "-loglevel", "error", "-f", "gdigrab", "-draw_mouse", "1", "-framerate", str(self.fps),
                                      "-offset_x", str(x), "-offset_y", str(y), "-video_size", f"{w}x{h}", "-i", "desktop",
                                      "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", self.seg_file],
                                     stdin=subprocess.PIPE)
        time.sleep(1.5)

    def stop(self):
        time.sleep(0.8)
        self.proc.stdin.write(b"q"); self.proc.stdin.flush()
        wall_end = time.time()
        self.proc.wait(timeout=60); self.proc = None
        dur = float(subprocess.run([FFMPEG.replace("ffmpeg.exe", "ffprobe.exe"), "-v", "error", "-show_entries", "format=duration",
                                    "-of", "csv=p=0", self.seg_file], capture_output=True, text=True).stdout.strip())
        self.segments.append((self.seg_file, wall_end, dur))

    pause = stop
    resume = start

    def video_time(self, wall):
        """Đổi thời điểm thực sang thời điểm trong video đã ghép các đoạn."""
        start = 0.0
        for _, wall_end, dur in self.segments:
            if wall <= wall_end:
                return max(start, start + dur - (wall_end - wall))
            start += dur
        return start

    # ----- bước có phụ đề -----
    def step(self, caption, action=None, hold=None):
        """Hiện phụ đề, làm thao tác, rồi giữ đủ thời gian đọc."""
        start = time.time()
        if action: action()
        need = hold if hold is not None else reading_time(caption)
        rest = need - (time.time() - start)
        if rest > 0: time.sleep(rest)
        self.steps.append((start, time.time(), caption))

    # ----- thao tác -----
    def move(self, x, y, duration=0.7):
        sx, sy = cursor()
        n = max(8, int(duration * 60))
        for i in range(1, n + 1):
            t = i / n; e = t * t * (3 - 2 * t)          # chậm - nhanh - chậm như tay người
            user32.SetCursorPos(int(sx + (x - sx) * e), int(sy + (y - sy) * e)); time.sleep(duration / n)

    def click(self, x, y, right=False, double=False):
        self.move(x, y); time.sleep(0.25)
        self.clicks.append((time.time(), x - self.region[0], y - self.region[1]))
        for _ in range(2 if double else 1):
            mouse_button(True, right); time.sleep(0.06); mouse_button(False, right); time.sleep(0.08)
        time.sleep(0.4)

    def click_name(self, name, hwnd=None, timeout=15, right=False, double=False):
        end = time.time() + timeout
        while time.time() < end:
            p = find_element(name, hwnd)
            if p: self.click(*p, right=right, double=double); return p
            time.sleep(0.5)
        raise RuntimeError(f"Không thấy phần tử '{name}'")

    def drag(self, x1, y1, x2, y2, duration=1.2):
        self.move(x1, y1); time.sleep(0.2)
        mouse_button(True); time.sleep(0.15)
        self.move(x2, y2, duration); time.sleep(0.15)
        mouse_button(False); time.sleep(0.4)

    def type(self, text, delay=0.045):
        for ch in text: unicode_char(ch); time.sleep(delay)

    def press(self, vk):
        key(vk); time.sleep(0.05); key(vk, up=True); time.sleep(0.2)

    def press_combo(self, mod, vk):
        """Tổ hợp phím, ví dụ Ctrl+A = press_combo(0x11, 0x41)."""
        key(mod); time.sleep(0.05); key(vk); time.sleep(0.05); key(vk, up=True); key(mod, up=True); time.sleep(0.2)

    def wait(self, seconds): time.sleep(seconds)

    def scroll(self, ticks):
        """Lăn chuột tại vị trí hiện tại; ticks dương là lăn lên."""
        i = INPUT(type=0); i.u.mi = MOUSEINPUT(0, 0, ctypes.c_ulong(120 * ticks & 0xFFFFFFFF).value, 0x0800, 0, 0); _send(i)
        time.sleep(0.4)

    def wait_ai(self, timeout=300):
        """Chờ lượt chat Claude Code (tiến trình claude.exe chạy trong thư mục claude-chat) bắt đầu rồi kết thúc."""
        ps = "@(Get-CimInstance Win32_Process -Filter \"Name='claude.exe'\" | Where-Object { $_.CommandLine -match 'claude-chat' }).Count"
        def running(): return subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout.strip() not in ("", "0")
        end = time.time() + 15
        while time.time() < end and not running(): time.sleep(0.5)
        end = time.time() + timeout
        while time.time() < end and running(): time.sleep(1)
        time.sleep(1.5)

    # ----- che -----
    def mask(self, x, y, w, h, pad=3, since=None, until=None, kind="blur", color=None):
        """Che một vùng màn hình (tọa độ màn hình). since/until: thời điểm thực (time.time()); None = từ đầu / tới cuối."""
        rx, ry, rw, rh = self.region
        x0, y0 = max(0, x - pad - rx), max(0, y - pad - ry)
        x1, y1 = min(rw, x + w + pad - rx), min(rh, y + h + pad - ry)
        if x1 > x0 and y1 > y0:
            m = dict(x=x0, y=y0, w=x1 - x0, h=y1 - y0, since=since, until=until, kind=kind)
            if color: m["color"] = color
            self.masks.append(m); return m

    def mask_names(self, pattern, hwnd, **kw):
        """Che mọi phần tử giao diện có tên khớp regex (nhãn tab, tiêu đề khung...)."""
        return [self.mask(*rect, **kw) for rect in find_all(pattern, hwnd) if rect[2] < 600 and rect[3] <= 30]

    def ocr_mask(self, pattern, since=None, until=None, pad=4):
        """Chụp màn hình lúc này, OCR, che mọi từ khớp regex (không phân biệt hoa thường)."""
        import re
        png = self.out + ".ocr.png"
        screen_png(png, self.region)
        found = [self.mask(x + self.region[0], y + self.region[1], w, h, pad=pad, since=since, until=until)
                 for x, y, w, h, text in ocr(png) if re.search(pattern, text, re.I)]
        os.remove(png)
        return [m for m in found if m]

    def find_text(self, pattern, timeout=15):
        """Tìm chữ trên màn hình bằng OCR; trả (x, y, w, h) tọa độ màn hình của từ đầu tiên khớp regex."""
        import re
        end = time.time() + timeout
        while True:
            png = self.out + ".find.png"
            screen_png(png, self.region)
            words = ocr(png); os.remove(png)
            for x, y, w, h, text in words:
                if re.search(pattern, text, re.I): return (x + self.region[0], y + self.region[1], w, h)
            if time.time() > end: raise RuntimeError(f"Không thấy chữ '{pattern}' trên màn hình")
            time.sleep(0.8)

    def click_text(self, pattern, dx=0, dy=0, **kw):
        x, y, w, h = self.find_text(pattern)
        self.click(x + w // 2 + dx, y + h // 2 + dy, **kw)
        return x, y, w, h

    def end_masks(self, masks):
        for m in masks: m["until"] = time.time()

    # ----- dựng -----
    def finish(self, title=None):
        if self.proc: self.stop()
        raw = self.out + ".raw.mp4"
        lst = self.out + ".segs.txt"
        open(lst, "w", encoding="utf-8").write("".join("file '" + f.replace("\\", "/") + "'\n" for f, _, _ in self.segments))
        subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", lst, "-c", "copy", raw], check=True)
        for f, _, _ in self.segments: os.remove(f)
        os.remove(lst)

        vt = self.video_time
        steps = [(vt(s), vt(e), c) for s, e, c in self.steps]
        clicks = [(vt(t), x, y) for t, x, y in self.clicks]
        masks = [dict(m, since=None if m.get("since") is None else vt(m["since"]),
                      until=None if m.get("until") is None else vt(m["until"])) for m in self.masks]
        w, h = self.region[2], self.region[3]
        ass = self.out + ".ass"
        write_ass(ass, w, h, title, steps, clicks)
        json.dump({"title": title, "steps": steps, "clicks": clicks, "masks": masks, "region": self.region},
                  open(self.out + ".json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        render(raw, ass, self.out, masks, w, h)          # giữ .raw.mp4 để dựng lại khi cần
        return self.out


def write_ass(ass, w, h, title, steps, clicks):
    def ts(t): return f"{int(t // 3600)}:{int(t % 3600 // 60):02d}:{t % 60:05.2f}"
    lines = ["[Script Info]", "ScriptType: v4.00+", f"PlayResX: {w}", f"PlayResY: {max(h, 1080)}", "",
             "[V4+ Styles]",
             "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV",
             "Style: Cap,Segoe UI,40,&H00FFFFFF,&H00000000,&H00000000,1,1,0,0,2,120,120," + str(max(20, (max(h, 1080) - h - 100) // 2)),
             "Style: Title,Segoe UI,64,&H00FFFFFF,&H00000000,&HC0101010,1,3,30,0,5,100,100,0",
             "Style: Ring,Segoe UI,10,&H000080FF,&H000080FF,&H00000000,0,1,5,0,7,0,0,0", "",
             "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text"]
    if title: lines.append(f"Dialogue: 2,{ts(0)},{ts(4)},Title,,0,0,0,,{{\\pos({w // 2},{h // 2})}}{title}")
    for s, e, cap in steps:
        lines.append(f"Dialogue: 1,{ts(s)},{ts(e)},Cap,,0,0,0,,{cap}")
    for t, x, y in clicks:                      # vòng tròn cam nở ra ở chỗ bấm
        lines.append(f"Dialogue: 3,{ts(t)},{ts(t + 0.7)},Ring,,0,0,0,,"
                     f"{{\\pos({x},{y})\\an5\\1a&HFF&\\bord5\\fscx40\\fscy40\\t(0,700,\\fscx130\\fscy130\\3a&HFF&)\\p1}}"
                     f"m -30 0 b -30 -40 30 -40 30 0 b 30 40 -30 40 -30 0{{\\p0}}")
    open(ass, "w", encoding="utf-8").write("\n".join(lines) + "\n")


def render(raw, ass, out, masks, w, h):
    """Dựng video cuối từ bản quay thô + phụ đề ASS. Dựng lại riêng: python recorder.py --render ra.mp4 [--ass]"""
    esc = ass.replace("\\", "/").replace(":", "\\:")
    chain, last = [], "0:v"
    for k, m in enumerate(masks):
        mx, my = m["x"] // 2 * 2, m["y"] // 2 * 2
        mw, mh = min((m["w"] + 1) // 2 * 2, w - mx), min((m["h"] + 1) // 2 * 2, h - my)
        a, b = m.get("since"), m.get("until")
        enable = "" if a is None and b is None else f":enable='between(t,{a or 0:.2f},{b if b is not None else 99999:.2f})'"
        if m.get("kind") == "fill":                      # phủ kín bằng màu (vùng lớn như thanh điều hướng Explorer)
            chain.append(f"[{last}]drawbox=x={mx}:y={my}:w={mw}:h={mh}:color={m.get('color', '0xF7F7F7')}:t=fill{enable}[v{k}]")
        elif m.get("kind") == "delogo":
            chain.append(f"[{last}]delogo=x={mx}:y={my}:w={mw}:h={mh}{enable}[v{k}]")
        else:
            lr = max(1, min(10, mh // 2 - 1)); cr = max(1, min(lr, mh // 4 - 1))
            chain.append(f"[{last}]split[b{k}][c{k}];[c{k}]crop={mw}:{mh}:{mx}:{my},boxblur={lr}:3:{cr}:3[m{k}];"
                         f"[b{k}][m{k}]overlay={mx}:{my}{enable}[v{k}]")
        last = f"v{k}"
    chain.append(f"[{last}]pad={w}:{max(h, 1080)}:0:0:black,ass='{esc}'[out]")
    script = out + ".filter.txt"
    open(script, "w", encoding="utf-8").write(";".join(chain))
    subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", raw, "-/filter_complex", script, "-map", "[out]",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out], check=True)
    os.remove(script)


def main():
    if sys.argv[1] == "--render":                       # dựng lại sau khi sửa vùng che/phụ đề trong .json (--ass) hoặc .ass
        out = os.path.abspath(sys.argv[2]); meta = json.load(open(out + ".json", encoding="utf-8"))
        w, h = meta["region"][2], meta["region"][3]
        if "--ass" in sys.argv: write_ass(out + ".ass", w, h, meta.get("title"), meta["steps"], meta["clicks"])
        render(out + ".raw.mp4", out + ".ass", out, meta["masks"], w, h); print(out); return
    script, out = sys.argv[1], os.path.abspath(sys.argv[2])
    sys.path.insert(0, HERE)
    spec = importlib.util.spec_from_file_location("kich_ban", script); mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
    r = Recorder(out, getattr(mod, "REGION", REGION))
    if hasattr(mod, "prepare"): mod.prepare(r)
    r.start()
    error = None
    try: mod.run(r)
    except Exception as ex:                             # vẫn dựng phần đã quay để xem lại hoặc ghép
        import traceback; traceback.print_exc(); error = ex
    finally:
        if r.proc: r.stop()
    print(r.finish(getattr(mod, "TITLE", None)))
    if error: sys.exit(1)


if __name__ == "__main__":
    main()
