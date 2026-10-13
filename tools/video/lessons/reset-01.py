"""Đưa máy về trạng thái đầu cho video 01-cai-dat (sau một lần quay hỏng)."""
import os, re, shutil, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import recorder as R

for h, t, _ in R.windows():
    if re.search(r"^CaiDat|^vn-revit-mcp-|cmd\.exe", t): R.user32.PostMessageW(h, 0x10, 0, 0)
time.sleep(1.5)
shutil.rmtree(r"C:\CaiDat\vn-revit-mcp-0.7.9-win-x64", ignore_errors=True)
h = R.find_window(r"Release vn-revit-mcp .*Chrome")
R.place(h); R.focus(h)
R.key(0x74); R.key(0x74, up=True); time.sleep(4)
R.key(0x24); R.key(0x24, up=True)
print(os.listdir(r"C:\CaiDat"))
