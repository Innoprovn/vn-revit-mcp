---
name: dim-thep
title: Dim khoảng cách thép (bám thẳng vào thanh thép)
keywords: dim, dimension, rebar, thép, kích thước, khoảng cách, multi-rebar annotation
verified: Revit 2026, 13/10/2026 (dải 9 thanh, 8 đoạn 350 mm, reference đều là thép)
---
## Cách làm
1. Mở view thấy rõ các thanh (mặt bằng sàn hoặc mặt cắt): open_view.
2. Gọi dimension_rebar với element_ids là dải thép (hoặc cấu kiện chứa thép); offset_mm để đặt đường dim xa mép.
3. Kiểm tra bằng capture_view với element_ids là id dim trả về.

## Vì sao không vẽ line giả
Revit API có MultiReferenceAnnotation ("Multi-rebar annotation"): dim + tag trỏ thẳng reference của thanh thép,
tự cập nhật khi thép đổi. NewDimension với reference lấy từ hình học thép thường null, dim dài 0 hoặc không hiện.

## Bẫy
- Phương rải thép vuông góc mặt phẳng view thì không dim được: đổi sang view nhìn thấy các thanh xếp cạnh nhau.
- Kiểu dim mặc định có thể ghi tổng chiều dài; dự án thật truyền annotation_type là Multi-Rebar Annotation Type của công ty.
- Đơn vị hiển thị theo dự án; đọc giá trị thật bằng mm qua kết quả spacing_mm.

## Tự viết bằng API (khi cần tùy biến)
MultiReferenceAnnotationOptions(type): SetElementsToDimension([rebarId]), DimensionStyleType=Linear,
DimensionLineDirection = phương rải chiếu lên mặt view, DimensionPlaneNormal = view.ViewDirection, DimensionLineOrigin;
kiểm AreReferencesValidForLinearDimension rồi MultiReferenceAnnotation.Create(doc, viewId, options).

## Nguồn
- Revit API: MultiReferenceAnnotation, MultiReferenceAnnotationOptions (tra revit_api).
- The Building Coder 1021 "MultiReferenceAnnotation Example"; 1684 (các cách dim thép qua reference hình học và nhược điểm).
