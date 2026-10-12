# Cửa hàng lệnh cộng đồng

Các lệnh ở đây hiện trong nút **Cửa hàng lệnh** trên tab InnoPro của Revit. Người dùng xem mã, bấm **Cài**,
lệnh có ngay (nút trên ribbon và AI gọi được), không cần build hay cài đặt gì thêm.

## Đóng góp một lệnh (5 bước)

1. Viết lệnh trong **một file `.cs`** theo [docs/viet-lenh-moi.md](../docs/viet-lenh-moi.md)
   (hoặc nhờ AI: dùng công thức `viet-lenh-moi`). Thử trên máy mình bằng cách thả vào thư mục Scripts.
2. Fork repo, chép file vào `community/scripts/`. Tên file tiếng Việt không dấu, PascalCase, ví dụ `KiemTraPhong.cs`.
3. Thêm một mục vào `community/index.json`:

   ```json
   {
     "id": "ten_lenh_snake_case",
     "file": "scripts/TenFile.cs",
     "title": "Tên ngắn hiển thị",
     "description": "Một câu: lệnh làm gì.",
     "category": "Nhóm trên ribbon",
     "author": "Tên bạn"
   }
   ```

   `id` phải trùng với `Name` trong lớp lệnh và không trùng lệnh có sẵn.
4. Chạy kiểm tra trên máy (cần .NET SDK 8): `powershell -File scripts\check-community.ps1`.
5. Mở pull request. Mỗi PR được người duyệt đọc mã trước khi đưa vào cửa hàng.

## Quy định

- Lệnh chỉ dùng Revit API và `InnoPro.Revit.Sdk`. **Không** gọi mạng, không chạy chương trình khác,
  không đọc/ghi file ngoài thư mục người dùng chỉ định, không thu thập dữ liệu.
- Lệnh sửa mô hình dùng `TransactionMode.Auto` để người dùng Undo được.
- Mô tả và thông báo lỗi bằng tiếng Việt có dấu.
- Đóng góp vào thư mục này theo GPLv3 như phần còn lại của repo.
