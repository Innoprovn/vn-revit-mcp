---
name: dung-tang-luoi-truc
description: Dựng hệ tầng và lưới trục từ mô tả bằng lời (số tầng, chiều cao tầng, bước trục).
arguments:
  - mo_ta*: Mô tả, ví dụ "10 tầng cao 3300, tầng 1 cao 4500; trục 1-6 bước 8000; trục A-D bước 7500".
---
Dựng tầng và lưới trục cho mô hình Revit đang mở theo mô tả: {{mo_ta}}

1. Gọi list_levels để biết tầng đã có (tránh trùng tên, giữ cao độ ±0.000).
2. Tính danh sách tầng (tên, cao độ mm) và lưới trục (tên, điểm đầu, điểm cuối mm; trục số theo phương X, trục chữ theo phương Y,
   mỗi trục kéo dài 2000 mm ra ngoài trục biên).
3. Trình bày bảng tầng và bảng trục cho người dùng, HỎI XÁC NHẬN trước khi tạo.
4. Khi đồng ý: gọi create_levels một lần với cả danh sách (plan_view=structural nếu là mô hình kết cấu, ngược lại floor),
   rồi create_grids một lần với cả danh sách. Báo lại số phần tử đã tạo và lỗi (nếu có).
