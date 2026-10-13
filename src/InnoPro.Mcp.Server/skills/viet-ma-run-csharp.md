---
name: viet-ma-run-csharp
title: Viết mã Revit API (run_csharp) đúng ngay lần đầu
keywords: run_csharp, code, mã, api, script, viết lệnh
verified: Revit 2025/2026, 13/10/2026
---
## Cách làm
1. Tra API trước: revit_api query="..." (có ghi năm có mặt 2025/2026); xem lớp kèm lọc: revit_api id="Autodesk.Revit.DB.X" query="từ khóa".
2. Thân mã nhận sẵn: doc, uidoc, uiapp, print(object). return giá trị sẽ thành kết quả.
3. mode=read (mặc định) chạy rồi hoàn tác; mode=write cần summary, người dùng duyệt trên màn hình.
4. Đơn vị nội bộ là feet: mm / 304.8. UnitUtils.ConvertFromInternalUnits để đổi.

## Bẫy
- Hàm in là print (chữ thường), không phải Print.
- Đổi ActiveView trong transaction bị cấm; dùng uidoc.RequestViewChange.
- Việc lặp lại nhiều lần: viết thành lệnh .cs trong thư mục Scripts (xem prompt viet-lenh-moi) để lần sau gọi 1 lần.