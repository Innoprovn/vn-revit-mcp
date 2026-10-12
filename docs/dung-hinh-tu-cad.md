# Dựng hình từ bản vẽ CAD

Nhóm **Dựng hình CAD** dựng cột, dầm, vách, tường, sàn, cọc, cửa từ bản vẽ DWG/DXF đã Link vào Revit.
Tên cấu kiện (D1X-3, C1, FF1...) được đọc thẳng từ chữ trong file CAD, **không cần explode chữ**.

## Chuẩn bị

1. **Link CAD** (Insert → Link CAD) vào mặt bằng tầng cần dựng, đơn vị mm. Nên Link thay vì Import:
   Import không còn file gốc nên không đọc được chữ.
2. Mỗi tầng một file mặt bằng đã tách sẵn (mặt bằng cột, mặt bằng dầm sàn...) cho kết quả gọn nhất.
3. Có Grid trong Revit thì dầm được chia nhịp theo Grid; chưa có thì chia theo cột đã dựng.

## Thứ tự nên làm

| Bước | Lệnh | Ghi chú |
|---|---|---|
| 1 | **Cột CAD** | Click vào đường bao cột. Tự tạo type theo kích thước, tên C1, C2... ghi vào Mark. |
| 2 | **Dầm CAD** | Click vào nét dầm. Tên và tiết diện đọc từ chữ `D1X-3(200x600)`. |
| 3 | **Vách / Tường CAD** | Tường hai nét; bề dày lệch vài mm được gộp (97, 103 → 100). |
| 4 | **Sàn/trần CAD** | Theo vùng hatch, tên vùng là chữ nằm trong hatch. |
| 5 | **Cửa CAD** | Cần tường đã có. Tên là ký hiệu (FF1, WF2, Đ1). |

Dựng cột trước để dầm chia nhịp đúng tại cột và kéo tới mặt cột.

## Cách lệnh đọc tên cấu kiện

- **Leader** chỉ vào cấu kiện nào thì tên thuộc cấu kiện đó.
- Không có leader: chữ phải nằm trong vùng quanh cấu kiện (dọc theo dầm, sát cột, quanh lỗ cửa), ưu tiên chữ
  nằm song song với cấu kiện.
- Dầm, vách: bề rộng ghi trên chữ phải khớp bề rộng đo từ hai nét, nên chữ của dầm bên cạnh không bị lấy nhầm.
- Cửa: bỏ qua chữ kích thước (900x2200), cao độ, tên phòng.
- Chữ font VNI (VNI-Times) hoặc TCVN3 (.VnTime) tự chuyển sang Unicode.

## Khi dùng với AI

Nói với AI kiểu: *"Dựng cột và dầm tầng 1 từ CAD đang link, layer dầm là AAT-Bao thay"*. AI sẽ:

1. `list_cad_layers` để xem layer nào chứa gì.
2. Chạy lệnh với `dry_run: true`: xem trước nhóm, tên, tiết diện, type sẽ dùng (dầm có thêm bảng chi tiết từng đoạn).
3. Chạy thật khi kết quả hợp lý.

Bản vẽ nhiều chữ: truyền `text_layers` (layer chữ tên cấu kiện, ví dụ `DCE_Name`) cho kết quả chắc chắn hơn.

## Khi không đọc được chữ

Lệnh vẫn chạy với Text Note trong view và báo lý do trong `tag_source`. Thường gặp:

- **CAD Import** thay vì Link: Link lại file.
- **File DWG lỗi** (khoảng 2% file): mở bằng AutoCAD, Save As lại rồi Reload link.
- Chữ nằm quá xa cấu kiện: tăng `text_search_mm`.
