---
name: tien-do-4d
title: Lập tiến độ và mô phỏng 4D
keywords: 4d, tiến độ, mô phỏng, gantt, schedule, thi công, timeline
verified: Revit 2026, 13/10/2026 (27 công việc, hiển thị theo ngày đúng)
---
1. Chưa có lệnh sim4d_*: marketplace_install id=sim4d (cài trong vài giây, không tắt Revit).
2. Tự sinh: sim4d_generate (group_by category|parameter|filter, by_level, start, days); dry_run=true để xem trước.
3. Tiến độ từ người dùng (Excel, mô tả): soạn danh sách {name,start,end,type,element_ids} rồi sim4d_set_tasks;
   lấy element_ids bằng find_elements output=ids theo category/level.
4. Xem tại một ngày: sim4d_show date=yyyy-MM-dd (view 3D "4D Simulation"), rồi capture_view view="4D Simulation".
5. Người dùng muốn tự chỉnh: sim4d_open mở cửa sổ Gantt.