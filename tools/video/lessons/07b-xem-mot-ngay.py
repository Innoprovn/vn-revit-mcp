"""Video 07, phần b: sau khi mô phỏng chạy hết (cửa sổ tiến độ đang kéo xuống dưới), kéo thanh thời gian xem một ngày.
Ghép sau phần a (07-mo-phong-4d.py, cắt tới lúc mô phỏng dừng).
"""
import ctypes, ctypes.wintypes as W, time
import recorder as R

TITLE = None


def rect(h):
    rc = W.RECT(); R.user32.GetWindowRect(h, ctypes.byref(rc)); return rc


def prepare(r):
    h = r.hwnd = R.revit_hwnd()
    r.mask_names(r"^(InnoPro|BIMTech)$", h)
    r.mask(1604, 4, 92, 24)                                     # tên tài khoản Autodesk
    r.g = R.find_window(r"^4D Simulation - ")
    R.focus(r.g)
    r.move(900, 500); r.clicks.clear()


def run(r):
    g = r.g; gr = rect(g)
    # Cửa sổ tiến độ đang ở dưới: kéo lên tới vị trí gốc (đỉnh y = 196).
    up = 196 - gr.top
    def scrub():
        r.drag(gr.left + 560, gr.top + 14, gr.left + 560, 196 + 14)
        r.wait(0.8)
        r.drag(gr.left + 608, 196 + 587, gr.left + 350, 196 + 587, duration=2.0)   # nút kéo thanh thời gian: cuối → giữa
        r.wait(2)
        r.drag(gr.left + 560, 196 + 14, gr.left + 560, gr.top + 14)               # hạ cửa sổ xuống để xem mô hình
    r.step("Bước 4. Kéo thanh thời gian để xem công trình tại một ngày bất kỳ.", scrub, hold=6)
    r.step("Mô hình hiển thị đúng phần đã thi công tính tới ngày đang chọn.", hold=7)
    r.step("Có thể hỏi AI trong khung chat, ví dụ: cho tôi xem công trình ngày 15/06/2028. AI tự bật view và hiển thị đúng ngày đó.", hold=9)
    r.step("Kết thúc phần hướng dẫn mô phỏng 4D. Tiến độ lưu trong file Revit, gửi file cho người khác vẫn xem được.", hold=7)
