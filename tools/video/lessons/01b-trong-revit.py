"""Video 01, phần b: Revit đã mở sau khi cài; xem tab lệnh, mở chat, chọn Claude Code, hỏi thử.
Chuẩn bị: Revit 2026 đang mở video/mau/Chung-cu-mau.rvt, vn-revit-mcp vừa cài (dữ liệu người dùng mới).
Ghép sau phần a (01-cai-dat.py) bằng ffmpeg concat.
"""
import time
import recorder as R

TITLE = None
SENSITIVE = r"inno|bimtech|btc|hai\.?vo|vo088|phuc|pdc"


def center(rect):
    x, y, w, h = rect
    return x + w // 2, y + h // 2


def locate(r, name, hwnd, ocr=None):
    """Vị trí nút: UI Automation trước, không được thì OCR."""
    for _ in range(3):
        rc = R.element_rect(name, hwnd)
        if rc: return rc
        time.sleep(0.7)
    return r.find_text(ocr or "^" + name.split()[0] + "$")


def open_tab(r, tab, h):
    """Bấm tab; Revit đôi khi nuốt cú bấm đầu (cửa sổ vừa được kích hoạt): bấm lại tới khi thấy nút Chat AI."""
    for _ in range(3):
        r.click(*center(tab)); time.sleep(1.2)
        if R.element_rect("Chat AI", h): return


def prepare(r):
    h = r.hwnd = R.revit_hwnd()
    R.place(h); R.focus(h)
    r.click_name("Architecture", h); time.sleep(0.5)
    r.move(900, 500); r.clicks.clear()


def run(r):
    h = r.hwnd
    t0 = time.time()
    r.mask_names(r"^(InnoPro|BIMTech)$", h, since=t0)
    r.ocr_mask(SENSITIVE, since=t0)
    tab = [b for b in R.find_all(r"^InnoPro$", h) if b[1] < 60][0]
    r.step("Revit đã mở. Trên ribbon có thêm tab lệnh của công cụ.", lambda: open_tab(r, tab, h), hold=5)
    r.wait(1)
    chat, shop = locate(r, "Chat AI", h, "^Chat$"), locate(r, "Cửa hàng", h, "^C.a$")
    r.step("Nhóm AI gồm Chat AI, Cửa hàng và Hệ thống. Các nhóm còn lại là lệnh làm việc với mô hình.",
           lambda: (r.move(*center(chat)), r.wait(1.5), r.move(*center(shop)), r.wait(1.5), r.move(900, 90)), hold=8)

    t_chat = time.time()
    r.step("Bước 6. Bấm Chat AI để mở khung chat.", lambda: (r.click(*center(chat)), r.wait(2.5)), hold=4)
    r.ocr_mask(SENSITIVE, since=t_chat)

    def pick():
        x, y, w, hh = r.find_text(r"^Claude$")                    # ô nhà cung cấp, mặc định "Claude Desktop (Free/Pro/Max)"
        r.click(x + 60, y + hh // 2); r.wait(1.2)
        r.move(x + 60, y + 120); r.scroll(5); r.wait(0.5)
        r.click_name("Claude Code (tài khoản đang đăng nhập)")
    r.step("Chọn Claude Code (tài khoản đang đăng nhập): dùng Claude Code đã đăng nhập trên máy, không cần API key.", pick, hold=7)
    q = "Revit đang mở dự án nào, mô hình có bao nhiêu tầng?"

    def ask():
        x, y, w, hh = [b for b in R.find_all(r"^G.i$", R.revit_hwnd()) if b[2] > 30][0]     # nút Gửi
        r.click(x - 120, y + hh // 2); r.type(q); r.wait(0.5); r.press(0x0D)
    r.step("Gõ câu hỏi vào ô dưới cùng rồi nhấn Enter.", ask, hold=4)
    r.step("AI tự gọi lệnh đọc mô hình rồi trả lời. Chờ vài giây.", r.wait_ai)
    r.ocr_mask(SENSITIVE, since=t_chat)
    r.step("Câu trả lời hiện trong khung chat. Công cụ đã cài đặt và kết nối thành công.", hold=6)
    r.step("Từ nay công cụ tự cập nhật, không phải cài lại. Video tiếp theo: mô phỏng 4D theo tiến độ.", hold=6)
