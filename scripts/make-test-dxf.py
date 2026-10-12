# Sinh file DXF R12 mẫu (đơn vị mm) để thử các lệnh Dựng hình CAD.
# Cách dùng: python scripts/make-test-dxf.py mau.dxf, rồi Link CAD file này vào mặt bằng (Insert > Link CAD, đơn vị mm).
# Layer: TUONG (tường 2 nét), COT (cột 400x400), DAM (tim dầm), SAN (biên sàn), COC (cọc D300), CUA (lỗ cửa), VACH (vách).
import sys
out = []
def g(code, val): out.append(f"{code}\n{val}")
def line(layer, x1, y1, x2, y2):
    g(0, "LINE"); g(8, layer); g(10, x1); g(20, y1); g(30, 0); g(11, x2); g(21, y2); g(31, 0)
def pline(layer, pts):
    g(0, "POLYLINE"); g(8, layer); g(66, 1); g(70, 1)
    for x, y in pts:
        g(0, "VERTEX"); g(8, layer); g(10, x); g(20, y); g(30, 0)
    g(0, "SEQEND"); g(8, layer)
def rect(layer, cx, cy, w, h):
    pline(layer, [(cx-w/2, cy-h/2), (cx+w/2, cy-h/2), (cx+w/2, cy+h/2), (cx-w/2, cy+h/2)])
def circle(layer, x, y, r):
    g(0, "CIRCLE"); g(8, layer); g(10, x); g(20, y); g(30, 0); g(40, r)
def text(layer, x, y, h, s, rot=0):
    g(0, "TEXT"); g(8, layer); g(10, x); g(20, y); g(30, 0); g(40, h); g(1, s); g(50, rot)

layers = ["TUONG", "COT", "DAM", "SAN", "COC", "CUA", "VACH"]
g(0, "SECTION"); g(2, "HEADER"); g(9, "$INSUNITS"); g(70, 4); g(0, "ENDSEC")
g(0, "SECTION"); g(2, "TABLES"); g(0, "TABLE"); g(2, "LAYER"); g(70, len(layers))
for i, n in enumerate(layers):
    g(0, "LAYER"); g(2, n); g(70, 0); g(62, i + 1); g(6, "CONTINUOUS")
g(0, "ENDTAB"); g(0, "ENDSEC")
g(0, "SECTION"); g(2, "ENTITIES")

X, Y, T = 12000, 8000, 200  # nhà 12x8 m, tường 200
# Tường bao: hai nét song song, chừa lỗ cửa 900 ở cạnh dưới (x 5000..5900)
for (a, b) in [((0, 0), (5000, 0)), ((5900, 0), (X, 0))]:
    line("TUONG", a[0], a[1], b[0], b[1]); line("TUONG", a[0], T, b[0], T)
line("TUONG", 0, Y, X, Y); line("TUONG", 0, Y - T, X, Y - T)
line("TUONG", 0, 0, 0, Y); line("TUONG", T, T, T, Y - T)
line("TUONG", X, 0, X, Y); line("TUONG", X - T, T, X - T, Y - T)
# Cửa: cánh + cung
line("CUA", 5000, 0, 5000, 900); line("CUA", 5000, 0, 5900, 0); line("CUA", 5000, T, 5900, T)
# Cột 400x400 tại lưới 3x2 (6000 x 8000)
cols = [(x, y) for x in (0, 6000, 12000) for y in (0, 8000)]
for x, y in cols:
    rect("COT", x, y, 400, 400)
# Dầm: nét tim giữa các cột + tên dầm
for y in (0, 8000):
    for x0, x1 in ((0, 6000), (6000, 12000)):
        line("DAM", x0, y, x1, y); text("DAM", (x0 + x1) / 2 - 600, y + 150, 200, "D1 (220x500)")
for x in (0, 6000, 12000):
    line("DAM", x, 0, x, 8000); text("DAM", x + 150, 3500, 200, "D2 (220x400)", 90)
# Sàn: đường bao
pline("SAN", [(0, 0), (X, 0), (X, Y), (0, Y)])
text("SAN", 3000, 4000, 250, "S1")
# Cọc D300 dưới cột
for x, y in cols:
    circle("COC", x, y, 150)
# Vách lõi: hình chữ nhật 2000x250
rect("VACH", 3000, 6000, 2000, 250)
text("VACH", 2400, 6300, 200, "V1")

g(0, "ENDSEC"); g(0, "EOF")
open(sys.argv[1], "w", encoding="ascii").write("\n".join(out) + "\n")
print("ok", len(out))
