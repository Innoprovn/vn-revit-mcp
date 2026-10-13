"""Video: mô phỏng 4D (cài tiện ích từ Cửa hàng, tự sinh tiến độ, chạy mô phỏng, hỏi AI xem công trình tại một ngày).
Chuẩn bị: Revit 2026 đang mở video/mau/Chung-cu-mau.rvt (chưa có tiến độ, view {3D}), đã cài vn-revit-mcp,
chưa cài tiện ích Mô phỏng 4D, khung chat đang chọn Claude Code.
"""
import ctypes, ctypes.wintypes as W, time
import recorder as R

TITLE = "Mô phỏng 4D theo tiến độ thi công"
SENSITIVE = r"inno|bimtech|btc|hai\.?vo|vo088|phuc|pdc"


def rect(h):
    rc = W.RECT(); R.user32.GetWindowRect(h, ctypes.byref(rc)); return rc


def center(rc):
    x, y, w, h = rc
    return x + w // 2, y + h // 2


def prepare(r):
    h = r.hwnd = R.revit_hwnd()
    R.place(h); R.focus(h)
    r.click(1902, 218); time.sleep(0.8)                         # nút + của khung chat: xoá hội thoại cũ
    # Tìm sẵn vị trí (UIA chậm) và vùng che khi chưa quay.
    r.tab = [b for b in R.find_all(r"^InnoPro$", h) if b[1] < 60][0]
    r.click(*center(r.tab)); time.sleep(1.2)
    r.shop_btn = R.element_rect("Cửa hàng", h)
    r.mask_names(r"^(InnoPro|BIMTech)$", h)
    r.ocr_mask(SENSITIVE)
    r.click_name("Architecture", h); time.sleep(0.5)
    r.move(900, 500); r.clicks.clear()


def run(r):
    h = r.hwnd
    r.wait(4)
    r.step("Video này hướng dẫn mô phỏng 4D: gắn tiến độ thi công vào mô hình và xem công trình lớn lên theo thời gian, ngay trong Revit.", hold=8)

    # ----- Bước 1: cài tiện ích -----
    r.step("Bước 1. Cài tiện ích Mô phỏng 4D. Trên tab lệnh của công cụ, bấm Cửa hàng.",
           lambda: (r.click(*center(r.tab)), r.wait(1.2), r.click(*center(r.shop_btn))))
    store = R.find_window(r"^Cửa hàng$")
    s = rect(store)
    shop = r.ocr_mask(SENSITIVE, since=time.time())
    r.step("Chọn Mô phỏng 4D trong danh sách, rồi bấm Cài.",
           lambda: (r.click_name("Mô phỏng 4D", store), r.wait(1.5), r.click(s.right - 51, s.bottom - 26)), hold=5)
    r.wait(3)
    msg = [m for m in R.windows() if m[1] == "Cửa hàng" and m[0] != store]
    shop += r.ocr_mask(SENSITIVE, since=time.time())
    def close_store():
        if msg:
            mr = rect(msg[0][0]); r.click(mr.right - 58, mr.bottom - 24); r.wait(1)
        r.click(s.right - 17, s.top + 18); r.wait(1.5)
    r.step("Cài xong trong vài giây, không cần khởi động lại Revit.", close_store, hold=5)
    r.end_masks(shop)

    # ----- Mở cửa sổ 4D -----
    def open_4d():
        btn = sorted([b for b in R.find_all(r"^Mô phỏng", h) if b[1] < 140], key=lambda b: b[2] * b[3])   # nút, không phải cả panel
        if btn: r.click(*center(btn[0]))
        else: r.click_name("Lệnh mới", h); r.wait(1); r.click_name("Mô phỏng 4D")
    r.step("Tab lệnh có thêm nhóm Mô phỏng 4D. Bấm nút Mô phỏng 4D để mở cửa sổ tiến độ.", open_4d)
    g = R.find_window(r"^4D Simulation - ")
    R.focus(g); gr = rect(g)
    G = lambda dx, dy: (gr.left + dx, gr.top + dy)
    r.step("Cửa sổ gồm ba phần: bên trái là tự sinh tiến độ, ở giữa là bảng tiến độ và biểu đồ Gantt, phía dưới là thanh chạy mô phỏng.",
           lambda: (r.move(*G(140, 200)), r.wait(1.5), r.move(*G(700, 300)), r.wait(1.5), r.move(*G(400, 580))), hold=9)

    # ----- Bước 2: lập tiến độ -----
    r.step("Bước 2. Lập tiến độ. Cách nhanh nhất là tự sinh: chia theo hạng mục và theo tầng, thi công từ dưới lên.",
           lambda: r.move(*G(120, 100)), hold=8)
    def set_date():
        r.click(*G(130, 256), double=True); r.press_combo(0x11, 0x41); r.type("01/03/2027"); r.press(0x09)
    r.step("Nhập ngày khởi công và số ngày cho mỗi công việc.", set_date, hold=5)
    r.step("Bấm Tạo tiến độ.", lambda: (r.click(*G(142, 332)), r.wait(3)), hold=4)
    r.step("Công cụ tạo 108 công việc: móng, dầm, sàn, cột, vách của từng tầng. Tiến độ được lưu ngay trong file Revit.",
           lambda: r.move(*G(600, 350)), hold=8)
    r.step("Bấm đúp vào ngày để sửa. Chuột phải vào công việc để chọn phần tử trong Revit, đổi loại hoặc xoá.",
           lambda: r.move(*G(520, 156)), hold=8)
    r.step("Ngoài cách tự sinh, có thể nhập tiến độ từ file, hoặc đưa file Excel, PDF tiến độ cho AI để gắn vào mô hình.", hold=8)

    # ----- Bước 3: chạy mô phỏng -----
    def speed():
        r.click(*G(1010, 566)); r.wait(1); r.click_name("1 tháng/giây"); r.wait(0.5)
        r.click(*G(825, 566))
    r.step("Bước 3. Chạy mô phỏng. Chọn tốc độ 1 tháng/giây và bật Camera bay quanh.", speed, hold=6)
    def play():
        r.click(*G(45, 577)); r.wait(1.5)
        r.drag(*G(560, 14), gr.left + 560, 905)                   # kéo cửa sổ xuống dưới để thấy toàn bộ view
    r.step("Bấm nút chạy, rồi kéo cửa sổ tiến độ xuống dưới để xem mô hình.", play, hold=5)
    r.step("Mô phỏng chạy trong một view 3D riêng, view gốc của bạn không bị thay đổi.", hold=7)
    r.step("Màu xanh là phần đang thi công. Phần đã xong hiện màu bình thường, phần chưa làm được ẩn đi.", hold=8)
    r.step("Camera xoay quanh công trình và nâng dần theo phần đang thi công.", hold=8)


    r.wait(6)                                                    # chờ mô phỏng chạy hết
    # ----- Bước 4: xem một ngày bất kỳ -----
    def scrub():
        r.drag(gr.left + 560, 905, *G(560, 14))                   # kéo cửa sổ tiến độ lên lại
        r.wait(0.8)
        r.drag(*G(800, 591), *G(470, 591), duration=2.0)          # kéo thanh thời gian về giữa tiến độ
        r.wait(1.5)
        r.drag(*G(560, 14), gr.left + 560, 905)                   # hạ cửa sổ xuống để xem mô hình
    r.step("Bước 4. Kéo thanh thời gian để xem công trình tại một ngày bất kỳ.", scrub, hold=6)
    r.step("Mô hình hiển thị đúng phần đã thi công tính tới ngày đang chọn.", hold=7)
    r.step("Có thể hỏi AI trong khung chat, ví dụ: cho tôi xem công trình ngày 15/06/2028. AI tự bật view và hiển thị đúng ngày đó.", hold=9)
    r.step("Kết thúc phần hướng dẫn mô phỏng 4D. Tiến độ lưu trong file Revit, gửi file cho người khác vẫn xem được.", hold=7)