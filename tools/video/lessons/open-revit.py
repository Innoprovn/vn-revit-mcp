"""Chờ Revit 2026 mở xong dự án mẫu: bấm Always Load (hộp thoại Unsigned Add-In của chính Revit 2026),
bỏ qua Unresolved References, rồi đặt cửa sổ vào vùng quay. Không đụng Revit bản khác đang mở trên máy.
Dùng: python lessons/open-revit.py [--launch]
"""
import os, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import recorder as R

PROJECT = r"C:\Users\BTC_TTTrong\Desktop\temp\khoaec\17\video\mau\Chung-cu-mau.rvt"
REVIT = r"C:\Program Files\Autodesk\Revit 2026\Revit.exe"
CLICKUI = os.path.join(R.HERE, "clickui.ps1")


def pids():
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "(Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*Revit 2026*' }).Id"],
                         capture_output=True, text=True).stdout
    return {int(x) for x in out.split() if x.isdigit()}


def click(h, name):
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", CLICKUI, "-Hwnd", str(h), "-Name", name])


if "--launch" in sys.argv:
    bat = os.path.join(os.environ["TEMP"], "video-run.cmd")
    open(bat, "w", encoding="ascii").write(f'@echo off\r\nstart "" "{REVIT}" "{PROJECT}"\r\n')
    subprocess.Popen(["explorer.exe", bat])      # chạy ngoài ứng dụng đóng gói
    time.sleep(10)

end = time.time() + 300
while time.time() < end:
    mine = pids()
    done = False
    for h, title, pid in R.windows():
        if pid not in mine: continue
        if title.startswith("Security - Unsigned Add-In"): click(h, "Always Load")
        elif title == "Unresolved References": click(h, "Ignore and continue opening the project")
        elif title.startswith("Autodesk Revit 2026") and "Chung-cu-mau" in title: done = True
    if done: break
    time.sleep(2)
else:
    sys.exit("Revit 2026 không mở xong")
# Hộp thoại có thể hiện muộn sau khi dự án đã mở (Unresolved References): quét thêm 15 giây.
for _ in range(8):
    time.sleep(2)
    for h, title, pid in R.windows():
        if pid in pids() and title == "Unresolved References": click(h, "Ignore and continue opening the project")
h = R.revit_hwnd(); R.place(h); R.focus(h)
print("Revit 2026 sẵn sàng")
