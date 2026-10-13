"""Lập chỉ mục blog The Building Coder (Jeremy Tammik, MIT) cho tool revit_api: số bài, tiêu đề, link, các lớp/hàm Revit API bài nhắc tới.
Chỉ lưu tiêu đề + link + tên API (không chép nội dung bài).

  git clone --filter=blob:none --no-checkout --depth 1 --branch gh-pages https://github.com/jeremytammik/tbc.git tbc
  cd tbc && git sparse-checkout init --no-cone && git sparse-checkout set "a/*.htm*" && git checkout gh-pages
  python scripts/build-tbc-index.py <thư mục tbc> <revit-api-....tsv.gz> src/InnoPro.Mcp.Server/knowledge/tbc-index.tsv.gz
"""
import gzip
import html
import re
import sys
from pathlib import Path

tbc_dir, api_index, output = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])

# Tên lớp và thành viên Revit API (đủ dài để không trùng từ thường).
names = set()
with gzip.open(api_index, "rt", encoding="utf-8") as f:
    for line in f:
        kind, doc_id = line.split("\t", 2)[:2]
        core = doc_id.split("(")[0]
        parts = core.split(".")
        if kind == "T":
            names.add(parts[-1])
        elif kind in ("M", "P", "E") and len(parts[-1]) >= 6:
            names.add(parts[-1])
names = {n for n in names if len(n) >= 4 and not n.startswith("#")}

token_re = re.compile(r"\b[A-Z][A-Za-z0-9_]{3,}\b")
rows = []
for page in sorted((tbc_dir / "a").glob("*.htm*")):
    m = re.match(r"(\d{4})_", page.name)
    if not m:
        continue
    raw = page.read_text(encoding="utf-8", errors="ignore")
    title = re.search(r"<h3>(.*?)</h3>", raw, re.S) or re.search(r"<title>(.*?)</title>", raw, re.S)
    title = re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", "", title.group(1)))).strip() if title else page.stem
    text = html.unescape(re.sub(r"<[^>]+>", " ", raw))
    found = []
    for token in token_re.findall(text):
        if token in names and token not in found:
            found.append(token)
    rows.append(f"{m.group(1)}\t{title}\thttps://jeremytammik.github.io/tbc/a/{page.name}\t{' '.join(found[:60])}\n")

output.parent.mkdir(parents=True, exist_ok=True)
with gzip.open(output, "wt", encoding="utf-8") as f:
    f.writelines(rows)
print(f"{len(rows)} bai -> {output} ({output.stat().st_size // 1024} KB)")
