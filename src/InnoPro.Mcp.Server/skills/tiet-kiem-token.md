---
name: tiet-kiem-token
title: Gọi lệnh gọn, tiết kiệm token
keywords: token, tiết kiệm, nhanh, gọn, đếm, thống kê, liệt kê
verified: Revit 2026 + Qwen 9B trên máy, 13/10/2026 (mỗi câu hỏi 1–2 lần gọi)
---
- Đếm theo nhóm: find_elements group_by=type|family|level|category (1 lần gọi thay vì liệt kê rồi tự đếm).
- Cần giá trị tham số: find_elements fields=["Area","Width"] (mm, m²) thay vì get_element_parameters từng phần tử.
- Chỉ cần id để chuyển sang lệnh khác: output=ids; chỉ cần số: output=count.
- Tra API: revit_api id=... kèm query để lọc thành viên (Rebar: 2.183 → 74 token).
- Đo thực tế: token_usage cho biết lệnh nào tốn nhất.
- Ảnh (capture_view) ~600 token cho 900 px: chỉ dùng khi cần thấy bố cục.