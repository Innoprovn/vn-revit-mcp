"""Video: trò chuyện với mô hình Revit bằng Claude Code đã đăng nhập (không cần API key).
Chuẩn bị: Revit 2025+ đã cài công cụ, mở dự án mẫu (video/mau/Du-an-mau.rvt), view 3D; Claude Code đã đăng nhập.
"""
import time
import recorder as R

TITLE = "Trò chuyện với mô hình Revit bằng Claude Code"
CHAT_X = 1766            # giữa khung chat bên phải (màn hình 1920 px, Revit phóng to)


def prepare(r):
    h = r.hwnd = R.revit_hwnd()
    R.focus(h)
    # Mở khung chat để lấy vị trí tiêu đề cần che, chọn sẵn nhà cung cấp khác, rồi đóng khung chat.
    r.click_name("InnoPro", h); time.sleep(0.8)
    r.click_name("Chat AI", h); time.sleep(1.5)
    r.mask_names(r"^(InnoPro|BIMTech|InnoPro AI)$", h)
    r.mask(1598, 2, 122, 22)                     # tên tài khoản Autodesk trên thanh tiêu đề
    r.click(1738, 211); time.sleep(0.8); r.press(0x24); r.press(0x28); r.press(0x28); r.press(0x0D)   # chọn "Claude API"
    time.sleep(0.8)
    r.click(1898, 212); time.sleep(0.5)          # nút + : cuộc trò chuyện mới
    r.click(1913, 181); time.sleep(0.8)          # nút đóng khung chat
    r.click_name("Architecture", h); time.sleep(0.5)
    r.move(960, 600); r.clicks.clear()


def run(r):
    h = r.hwnd
    r.wait(4)                                    # tiêu đề video
    r.step("Video này hướng dẫn trò chuyện với mô hình Revit bằng Claude Code đã đăng nhập trên máy. Cách này không cần API key.", hold=7)
    r.step("Chuẩn bị: đã cài công cụ (Bài 01), đã cài Claude Code và đăng nhập một lần. Revit đang mở một dự án.", hold=7)

    r.step("Bước 1. Mở tab lệnh của công cụ trên ribbon.", lambda: r.click_name("InnoPro", h))
    r.step("Bước 2. Bấm Chat AI. Khung chat hiện ở bên phải màn hình.", lambda: (r.click_name("Chat AI", h), r.wait(1)))
    r.step("Bước 3. Bấm ô chọn nhà cung cấp AI ở trên cùng khung chat.", lambda: r.click(1738, 211))

    def pick():
        r.move(1780, 300); r.scroll(5); r.wait(0.6)
        r.click_name("Claude Code (tài khoản đang đăng nhập)", h)
    r.step("Chọn mục Claude Code (tài khoản đang đăng nhập) ở đầu danh sách.", pick)
    r.step("Ô thứ hai là model. Mặc định là sonnet, phù hợp với hầu hết công việc.", lambda: r.move(1738, 248))

    q1 = "Mô hình có những tầng nào, mỗi tầng bao nhiêu cột?"
    def ask1():
        r.click(CHAT_X, 950); r.type(q1); r.wait(0.6); r.press(0x0D)
    r.step("Bước 4. Gõ câu hỏi bằng tiếng Việt vào ô dưới cùng, nhấn Enter để gửi.", ask1)
    r.step("Claude Code tự gọi các lệnh đọc mô hình. Các dòng chữ nhỏ là tên lệnh đang chạy. Chờ vài giây.", lambda: (r.move(CHAT_X, 520), r.wait_ai()))
    r.step("Câu trả lời hiện trong khung chat. Số liệu được đọc trực tiếp từ mô hình đang mở.", hold=6)

    q2 = "Tô màu đỏ các cột kết cấu trong view hiện tại"
    def ask2():
        r.click(CHAT_X, 950); r.type(q2); r.wait(0.6); r.press(0x0D)
    r.step("Bước 5. Yêu cầu AI thao tác trên mô hình, ví dụ tô màu các cột.", ask2)
    r.step("AI chọn lệnh phù hợp và chạy trong Revit. Mọi thay đổi đều hoàn tác được bằng Ctrl+Z.", lambda: (r.move(960, 560), r.wait_ai()))
    r.step("Kết quả hiện ngay trên view: các cột đã được tô màu đỏ.", lambda: r.move(1000, 640), hold=6)

    r.step("Hội thoại được nối tiếp: câu sau hiểu ngữ cảnh của câu trước. Bấm dấu + để bắt đầu cuộc trò chuyện mới.",
           lambda: r.move(1903, 211), hold=8)
    r.step("Lưu ý: câu hỏi và kết quả lệnh được gửi tới Claude theo tài khoản của bạn. Hạn mức tính theo gói đang dùng.", hold=8)
    r.step("Kết thúc phần hướng dẫn. Bài tiếp theo: hỏi đáp và thống kê mô hình.", hold=5)
