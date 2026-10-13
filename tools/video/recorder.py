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
    out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process Revit | Sort-Object StartTime -Descending | Select-Object -First 1).MainWindowHandle"], capture_output=True, text=True).stdout.strip()
    return int(out)


def focus(hwnd):
    """Đưa cửa sổ lên trước (nhấn Alt giả để Windows cho phép đổi cửa sổ nền trước)."""
    user32.ShowWindow(hwnd, 3)
    key(0x12); key(0x12, up=True)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.8)


def element_rect(name, hwnd=None):
    env = dict(os.environ, UIA_NAME=name, UIA_HWND=str(hwnd or ""))
    out = subprocess.run(["powershell", "-NoProfile", "-Command", UIA_PS], capture_output=True, text=True, env=env).stdout.strip()
    if not out or "," not in out: return None
    return tuple(map(int, out.splitlines()[-1].split(",")))


# ---------- cầu nối vn-revit-mcp (named pipe) ----------
def revit(name, args=None, pid=None, method="invoke"):
    import pathlib
    if pid is None:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process Revit | Sort-Object StartTime -Descending | Select-Object -First 1).Id"], capture_output=True, text=True).stdout.strip()
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


class Recorder:
    def __init__(self, out, region=(0, 0, 1920, 1040), fps=24):
        self.out, self.region, self.fps = out, region, fps
        self.steps, self.clicks, self.masks = [], [], []
        self.t0 = time.time()
        self.proc = None

    # ----- quay -----
    def start(self):
        x, y, w, h = self.region
        self.raw = self.out + ".raw.mp4"
        self.proc = subprocess.Popen([FFMPEG, "-y", "-loglevel", "error", "-f", "gdigrab", "-draw_mouse", "1", "-framerate", str(self.fps),
                                      "-offset_x", str(x), "-offset_y", str(y), "-video_size", f"{w}x{h}", "-i", "desktop",
                                      "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", self.raw],
                                     stdin=subprocess.PIPE)
        time.sleep(1.5)
        self.t0 = time.time()

    def stop(self):
        time.sleep(1.0)
        self.proc.stdin.write(b"q"); self.proc.stdin.flush(); self.proc.wait(timeout=60)

    def now(self): return time.time() - self.t0

    # ----- bước có phụ đề -----
    def step(self, caption, action=None, hold=None):
        """Hiện phụ đề, làm thao tác, rồi giữ đủ thời gian đọc."""
        start = self.now()
        if action: action()
        need = hold if hold is not None else reading_time(caption)
        rest = need - (self.now() - start)
        if rest > 0: time.sleep(rest)
        self.steps.append((start, self.now(), caption))

    # ----- thao tác -----
    def move(self, x, y, duration=0.7):
        sx, sy = cursor()
        n = max(8, int(duration * 60))
        for i in range(1, n + 1):
            t = i / n; e = t * t * (3 - 2 * t)          # chậm - nhanh - chậm như tay người
            user32.SetCursorPos(int(sx + (x - sx) * e), int(sy + (y - sy) * e)); time.sleep(duration / n)

    def click(self, x, y, right=False, double=False):
        self.move(x, y); time.sleep(0.25)
        self.clicks.append((self.now(), x - self.region[0], y - self.region[1]))
        for _ in range(2 if double else 1):
            mouse_button(True, right); time.sleep(0.06); mouse_button(False, right); time.sleep(0.08)
        time.sleep(0.4)

    def click_name(self, name, hwnd=None, timeout=15, right=False):
        end = time.time() + timeout
        while time.time() < end:
            p = find_element(name, hwnd)
            if p: self.click(*p, right=right); return p
            time.sleep(0.5)
        raise RuntimeError(f"Không thấy phần tử '{name}'")

    def type(self, text, delay=0.045):
        for ch in text: unicode_char(ch); time.sleep(delay)

    def press(self, vk):
        key(vk); time.sleep(0.05); key(vk, up=True); time.sleep(0.2)

    def wait(self, seconds): time.sleep(seconds)

    def mask(self, x, y, w, h, pad=3):
        """Làm mờ một vùng màn hình (tọa độ màn hình) suốt video."""
        rx, ry, rw, rh = self.region
        x0, y0 = max(0, x - pad - rx), max(0, y - pad - ry)
        x1, y1 = min(rw, x + w + pad - rx), min(rh, y + h + pad - ry)
        if x1 > x0 and y1 > y0: self.masks.append((x0, y0, x1 - x0, y1 - y0))

    def mask_names(self, pattern, hwnd):
        """Làm mờ mọi phần tử giao diện có tên khớp regex (nhãn tab, tiêu đề khung...)."""
        for rect in find_all(pattern, hwnd):
            if rect[2] < 600 and rect[3] <= 30: self.mask(*rect)

    def scroll(self, ticks):
        """Lăn chuột tại vị trí hiện tại; ticks dương là lăn lên."""
        i = INPUT(type=0); i.u.mi = MOUSEINPUT(0, 0, ctypes.c_ulong(120 * ticks & 0xFFFFFFFF).value, 0x0800, 0, 0); _send(i)
        time.sleep(0.4)

    def wait_ai(self, timeout=240):
        """Chờ lượt chat Claude Code (tiến trình claude.exe chạy trong thư mục claude-chat) bắt đầu rồi kết thúc."""
        ps = "@(Get-CimInstance Win32_Process -Filter \"Name='claude.exe'\" | Where-Object { $_.CommandLine -match 'claude-chat' }).Count"
        def running(): return subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout.strip() not in ("", "0")
        end = time.time() + 15
        while time.time() < end and not running(): time.sleep(0.5)
        end = time.time() + timeout
        while time.time() < end and running(): time.sleep(1)
        time.sleep(1.5)

    # ----- dựng -----
    def finish(self, title=None):
        ass = self.out + ".ass"
        w, h = self.region[2], self.region[3]
        def ts(t): return f"{int(t // 3600)}:{int(t % 3600 // 60):02d}:{t % 60:05.2f}"
        lines = ["[Script Info]", "ScriptType: v4.00+", f"PlayResX: {w}", f"PlayResY: {h}", "",
                 "[V4+ Styles]",
                 "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV",
                 "Style: Cap,Segoe UI,40,&H00FFFFFF,&H00000000,&H90000000,1,3,14,0,2,120,120,46",
                 "Style: Title,Segoe UI,64,&H00FFFFFF,&H00000000,&HC0101010,1,3,30,0,5,100,100,0",
                 "Style: Ring,Segoe UI,10,&H000080FF,&H000080FF,&H00000000,0,1,5,0,7,0,0,0", "",
                 "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text"]
        if title: lines.append(f"Dialogue: 2,{ts(0)},{ts(4)},Title,,0,0,0,,{title}")
        for s, e, cap in self.steps:
            lines.append(f"Dialogue: 1,{ts(s)},{ts(e)},Cap,,0,0,0,,{cap}")
        for t, x, y in self.clicks:                      # vòng tròn cam nở ra ở chỗ bấm
            lines.append(f"Dialogue: 3,{ts(t)},{ts(t + 0.7)},Ring,,0,0,0,,"
                         f"{{\\pos({x},{y})\\an5\\1a&HFF&\\bord5\\fscx40\\fscy40\\t(0,700,\\fscx130\\fscy130\\3a&HFF&)\\p1}}"
                         f"m -30 0 b -30 -40 30 -40 30 0 b 30 40 -30 40 -30 0{{\\p0}}")
        open(ass, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        json.dump({"steps": self.steps, "clicks": self.clicks, "masks": self.masks, "region": self.region},
                  open(self.out + ".json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        render(self.raw, ass, self.out, self.masks, w, h)          # giữ .raw.mp4 để dựng lại khi cần
        return self.out


def render(raw, ass, out, masks, w, h):
    """Dựng video cuối từ bản quay thô + phụ đề ASS. Dựng lại được riêng: python recorder.py --render ra.mp4"""
    esc = ass.replace("\\", "/").replace(":", "\\:")
    # Che các vùng không muốn lộ (tên tài khoản, nhãn riêng...) bằng làm mờ, rồi mới vẽ phụ đề.
    chain, last = [], "0:v"
    for k, (mx, my, mw, mh) in enumerate(masks):
        mx, my, mw, mh = mx // 2 * 2, my // 2 * 2, (mw + 1) // 2 * 2, (mh + 1) // 2 * 2
        lr = max(1, min(10, mh // 2 - 1)); cr = max(1, min(lr, mh // 4 - 1))
        chain.append(f"[{last}]split[b{k}][c{k}];[c{k}]crop={mw}:{mh}:{mx}:{my},boxblur={lr}:3:{cr}:3[m{k}];[b{k}][m{k}]overlay={mx}:{my}[v{k}]")
        last = f"v{k}"
    chain.append(f"[{last}]ass='{esc}',pad={w}:{max(h, 1080)}:0:0:black[out]")
    subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", raw, "-filter_complex", ";".join(chain), "-map", "[out]",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out], check=True)


def main():
    if sys.argv[1] == "--render":                       # dựng lại sau khi sửa .ass hoặc vùng che trong .json
        out = os.path.abspath(sys.argv[2]); meta = json.load(open(out + ".json", encoding="utf-8"))
        render(out + ".raw.mp4", out + ".ass", out, meta["masks"], meta["region"][2], meta["region"][3]); print(out); return
    script, out = sys.argv[1], os.path.abspath(sys.argv[2])
    spec = importlib.util.spec_from_file_location("kich_ban", script); mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
    r = Recorder(out, getattr(mod, "REGION", (0, 0, 1920, 1040)))
    if hasattr(mod, "prepare"): mod.prepare(r)
    r.start()
    try: mod.run(r)
    finally: r.stop()
    print(r.finish(getattr(mod, "TITLE", None)))


if __name__ == "__main__":
    main()
