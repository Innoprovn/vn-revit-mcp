"""Video: cài vn-revit-mcp từ đầu (tải zip, giải nén, chạy Cai-dat.cmd, mở Revit, thử chat).
Chuẩn bị (máy quay): đã gỡ sạch vn-revit-mcp; C:\\CaiDat chỉ chứa file zip bản mới nhất (giống hệt file trên GitHub);
Revit đóng; Chrome (cửa sổ khách) đang mở trang Releases mới nhất. Dự án mẫu: video/mau/Chung-cu-mau.rvt.
"""
import ctypes, ctypes.wintypes as W, os, re, subprocess, time
import recorder as R

TITLE = "Cài đặt vn-revit-mcp cho Revit"
PROJECT = r"C:\Users\BTC_TTTrong\Desktop\temp\khoaec\17\video\mau\Chung-cu-mau.rvt"
REVIT = r"C:\Program Files\Autodesk\Revit 2026\Revit.exe"
ZIP = "vn-revit-mcp-0.7.8-win-x64"
SENSITIVE = r"inno|^inn$|bimtech|btc|hai\.?vo|vo088|phuc|pdc"
NAV = (0, 138, 428, 760)                 # thanh điều hướng Explorer (lộ tên ổ mạng, thư mục riêng): phủ kín
CLICKUI = os.path.join(R.HERE, "clickui.ps1")


def center(rect):
    x, y, w, h = rect
    return x + w // 2, y + h // 2


def close(pattern):
    for h, title, _ in R.windows():
        if re.search(pattern, title): R.user32.PostMessageW(h, 0x10, 0, 0)


def outside(*cmd):
    """Chạy lệnh qua Explorer để tiến trình nằm ngoài ứng dụng đóng gói (MSIX) đang điều khiển máy."""
    bat = os.path.join(os.environ["TEMP"], "video-run.cmd")
    open(bat, "w", encoding="ascii").write("@echo off\r\nstart \"\" " + " ".join(f'"{c}"' for c in cmd) + "\r\n")
    subprocess.Popen(["explorer.exe", bat])


def prepare(r):
    close(r"^CaiDat|^vn-revit-mcp-")
    time.sleep(1)
    r.chrome = R.find_window(r"Release vn-revit-mcp .*Chrome")
    R.place(r.chrome); R.focus(r.chrome)
    r.move(960, 500)


def run(r):
    r.wait(4)                                                    # tiêu đề video
    page = r.ocr_mask(SENSITIVE, since=time.time())
    page.append(r.mask(1795, 44, 85, 38, since=time.time()))      # nút hồ sơ trình duyệt
    r.step("Video này hướng dẫn cài vn-revit-mcp từ đầu cho Revit 2025 trở lên. Công cụ miễn phí, không cần quyền quản trị.", hold=7)
    r.step("Bước 1. Mở trang Releases của vn-revit-mcp trên GitHub. Bản mới nhất có nhãn Latest.",
           lambda: r.move(*center(r.find_text(r"^Latest$"))), hold=6)

    def scroll_assets():
        r.move(960, 600)
        r.scroll(-5); r.scroll(-5)
    r.step("Kéo xuống mục Assets.", scroll_assets, hold=3)
    r.end_masks([m for m in page if m["y"] > 90])                # chữ trong trang đã cuộn đi; thanh địa chỉ vẫn che
    page += r.ocr_mask(SENSITIVE, since=time.time())
    r.step(f"Bước 2. Tải file {ZIP}.zip (khoảng 11 MB).", lambda: r.move(*center(r.find_text(r"win-x64\.zip"))), hold=6)
    r.step("Lưu ý: không tải Source code (mã nguồn). File .mcpb chỉ dành cho Claude Desktop.",
           lambda: r.move(*center(r.find_text(r"^Source$"))), hold=7)

    # ----- Explorer: giải nén -----
    def open_folder():
        subprocess.Popen(["explorer.exe", r"C:\CaiDat"])
        h = R.find_window(r"^CaiDat")
        R.place(h); R.focus(h)
        r.end_masks([m for m in page if m.get("until") is None])
    nav = r.mask(*NAV, pad=0, since=time.time(), kind="fill")
    r.step("Bước 3. Mở thư mục chứa file vừa tải về.", open_folder, hold=4)
    r.step("Chuột phải vào file zip, chọn Extract All.",
           lambda: (r.click_text(r"win-x64", right=True), r.wait(1.5), r.click_name("Extract All...")), hold=4)
    r.step("Bấm Extract để giải nén. Thư mục giải nén tự mở ra.",
           lambda: (r.wait(1.5), r.click_name("Extract"), r.wait(3)), hold=5)
    h2 = R.find_window(rf"^{ZIP}")
    R.place(h2); R.focus(h2); r.wait(1)
    cai = r.find_text(r"^Cai-")
    r.step("Thư mục gồm add-in cho Revit, chương trình kết nối AI và file Cai-dat.cmd để cài.",
           lambda: r.move(*center(cai)), hold=6)

    # ----- Chạy bộ cài -----
    def run_setup():
        before = {w[0] for w in R.windows()}
        r.click(*center(cai)); r.wait(0.5); r.press(0x0D)
        end = time.time() + 20
        con = None
        while con is None and time.time() < end:
            con = next((w[0] for w in R.windows() if w[0] not in before and re.search(r"cmd|Cai-dat|PowerShell|Terminal", w[1], re.I)), None)
            time.sleep(0.3)
        if con is None: raise RuntimeError("Bộ cài không mở")
        R.place(con); R.focus(con)
        r.end_masks([nav])
    t_con = time.time()
    r.step("Bước 4. Bấm đúp Cai-dat.cmd (hoặc chọn rồi nhấn Enter). Cửa sổ cài đặt chạy khoảng 10 đến 30 giây.", run_setup, hold=5)
    r.step("Bộ cài chép chương trình, đặt Revit luôn nạp công cụ và tự kết nối các ứng dụng AI có trên máy.", wait_setup, hold=7)
    console = r.ocr_mask(SENSITIVE + r"|users|appdata|\\local", since=t_con)
    r.step("Phần Kiểm tra ở cuối: dòng [OK] là ổn. Dòng [!!] là cần xử lý, ngay bên dưới có hướng dẫn cách sửa.", hold=8)
    r.step("Nhấn phím bất kỳ để đóng cửa sổ.", lambda: (r.press(0x20), r.wait(1)), hold=3)
    r.end_masks(console)
    nav = r.mask(*NAV, pad=0, since=time.time(), kind="fill")    # cửa sổ thư mục giải nén vẫn đang hiện

    # ----- Mở Revit (tạm dừng quay lúc Revit khởi động) -----
    r.step("Bước 5. Mở Revit và mở một dự án.", hold=4)
    r.pause()
    r.end_masks([nav])
    close(rf"^{ZIP}|^CaiDat|Chrome")
    outside(REVIT, PROJECT)
    dialog = wait_revit()
    if dialog:
        # Chỉ để lộ hộp thoại, phủ tối phần còn lại (màn hình nền lúc Revit đang khởi động).
        rc = W.RECT(); R.user32.GetWindowRect(dialog, ctypes.byref(rc))
        dark = dict(pad=0, kind="fill", color="0x202020", since=time.time())
        around = [r.mask(0, 0, 1920, rc.top, **dark), r.mask(0, rc.bottom, 1920, 920 - rc.bottom, **dark),
                  r.mask(0, rc.top, rc.left, rc.bottom - rc.top, **dark), r.mask(rc.right, rc.top, 1920 - rc.right, rc.bottom - rc.top, **dark)]
        r.resume()
        r.step("Nếu Revit hỏi có nạp add-in chưa ký số hay không, chọn Always Load.",
               lambda: (r.wait(1.5), r.click_name("Always Load", dialog)), hold=6)
        r.pause()
        r.end_masks([m for m in around if m])
        wait_revit(after_trust=True)
    h = R.revit_hwnd(); R.place(h); R.focus(h); r.wait(3)
    r.resume()
    t_rv = time.time()
    r.mask_names(r"^(InnoPro|BIMTech)$", h, since=t_rv)
    r.ocr_mask(SENSITIVE, since=t_rv)

    r.step("Revit đã mở. Trên ribbon có thêm tab lệnh của công cụ.", lambda: r.click_name("InnoPro", h), hold=5)
    r.step("Nhóm AI gồm Chat AI, Cửa hàng và Hệ thống. Các nhóm còn lại là lệnh làm việc với mô hình.",
           lambda: (r.move(*center(R.element_rect("Chat AI", h))), r.wait(1.2), r.move(*center(R.element_rect("Cửa hàng", h)))), hold=7)

    # ----- Thử chat -----
    t_chat = time.time()
    r.step("Bước 6. Bấm Chat AI để mở khung chat.", lambda: (r.click_name("Chat AI", h), r.wait(2.5)), hold=4)
    r.ocr_mask(SENSITIVE, since=t_chat)

    def pick():
        x, y, w, hh = r.find_text(r"^Claude$")                    # ô nhà cung cấp, mặc định "Claude Desktop (Free/Pro/Max)"
        r.click(x + 60, y + hh // 2); r.wait(1.2)
        r.move(x + 60, y + 120); r.scroll(5); r.wait(0.5)
        r.click_name("Claude Code (tài khoản đang đăng nhập)")
    r.step("Chọn Claude Code (tài khoản đang đăng nhập): dùng Claude Code đã đăng nhập trên máy, không cần API key.", pick, hold=7)
    q = "Revit đang mở dự án nào, mô hình có bao nhiêu tầng?"

    def ask():
        x, y, w, hh = r.find_text(r"^G(ử|u)i$")
        r.click(x - 120, y + hh // 2); r.type(q); r.wait(0.5); r.press(0x0D)
    r.step("Gõ câu hỏi vào ô dưới cùng rồi nhấn Enter.", ask, hold=4)
    r.step("AI tự gọi lệnh đọc mô hình rồi trả lời. Chờ vài giây.", r.wait_ai)
    r.ocr_mask(SENSITIVE, since=t_chat)
    r.step("Câu trả lời hiện trong khung chat. Công cụ đã cài đặt và kết nối thành công.", hold=6)
    r.step("Từ nay công cụ tự cập nhật, không phải cài lại. Video tiếp theo: mô phỏng 4D theo tiến độ.", hold=6)


def wait_setup(timeout=180):
    """Chờ bộ cài chạy xong (không còn powershell chạy Install-VnRevitMcp.ps1)."""
    ps = "@(Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -match 'Install-VnRevitMcp' }).Count"
    def running(): return subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout.strip() not in ("", "0")
    end = time.time() + 20
    while time.time() < end and not running(): time.sleep(0.5)
    end = time.time() + timeout
    while time.time() < end and running(): time.sleep(1)
    time.sleep(1.5)


def wait_revit(after_trust=False, timeout=300):
    """Chờ Revit mở xong dự án; trả hwnd hộp thoại "Unsigned Add-In" nếu nó hiện ra (để quay cảnh bấm Always Load)."""
    end = time.time() + timeout
    while time.time() < end:
        mine = revit2026_pids()
        for h, title, pid in R.windows():
            if pid not in mine: continue                         # bỏ qua Revit khác đang mở trên máy
            if title.startswith("Security - Unsigned Add-In") and not after_trust:
                return h
            if title == "Unresolved References":
                subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", CLICKUI, "-Hwnd", str(h),
                                "-Name", "Ignore and continue opening the project"])
            if title.startswith("Autodesk Revit") and "Chung-cu-mau" in title:
                time.sleep(5)
                return None
        time.sleep(1)
    raise RuntimeError("Revit không mở xong")


def revit2026_pids():
    ps = "(Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*Revit 2026*' }).Id"
    out = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout
    return {int(x) for x in out.split() if x.isdigit()}