---
name: dung-hinh-tu-cad
title: Dựng cột, dầm, tường, sàn, cửa từ bản vẽ CAD
keywords: cad, dwg, dxf, cột, dầm, tường, sàn, cửa, dựng hình, link cad
verified: Revit 2025, DWG thật dự án 3.GV.T.02.01, 12–13/10/2026 (dầm 23/23 đúng tên + tiết diện)
---
## Thứ tự
1. list_cad_links, list_cad_layers (level=...) để biết layer nào chứa gì.
2. Cột trước: place_columns_from_cad (tên C1.. từ chữ CAD ghi vào Mark; tự tạo type theo b×h).
3. Dầm: place_beams_from_cad dry_run=true xem bảng detail từng đoạn (tên, b, h, toạ độ) rồi chạy thật.
   Truyền text_layers (layer chữ tên dầm, vd DCE_Name) khi bản vẽ nhiều chữ.
4. Vách/tường: create_walls_from_cad (bề dày lệch ≤5 mm gộp; tự tạo type "InnoPro 200mm").
5. Sàn theo hatch; cửa cần tường đã có (tên là ký hiệu FF1, WF2...).

## Cách đọc tên
Chữ đọc thẳng từ file DWG/DXF (không cần explode), font VNI/TCVN3 tự chuyển Unicode.
Leader chỉ vào cấu kiện là chắc chắn; không có thì vùng chữ nhật theo cấu kiện, b trên chữ phải khớp b đo.

## Bẫy
- Phải là Link CAD (Import không còn file để đọc chữ).
- Không có Grid thì dầm chia nhịp theo cột đã dựng: dựng cột trước.
- 2% file DWG không đọc được: Save As lại bằng AutoCAD.