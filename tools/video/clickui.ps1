param([long]$Hwnd, [string]$Name)
# Bấm một phần tử (tìm theo tên UI Automation) trong cửa sổ $Hwnd bằng thông điệp chuột gửi thẳng vào cửa sổ con chứa nó.
# Không di chuyển chuột thật, cửa sổ bị che vẫn bấm được.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class C {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static IntPtr Deepest(IntPtr h, int sx, int sy) {
    while (true) { var p = new POINT { X = sx, Y = sy }; ScreenToClient(h, ref p);
      var c = ChildWindowFromPointEx(h, p, 1 | 4); if (c == IntPtr.Zero || c == h) return h; h = c; } }
  public static void Click(IntPtr h, int sx, int sy) {
    var p = new POINT { X = sx, Y = sy }; ScreenToClient(h, ref p);
    var l = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
    PostMessage(h, 0x200, IntPtr.Zero, l); PostMessage(h, 0x201, (IntPtr)1, l); PostMessage(h, 0x202, IntPtr.Zero, l); } }
'@
$dlg = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($Hwnd))
$el = $dlg.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $Name } | Select-Object -First 1
if (-not $el) { throw "Không thấy '$Name'." }
$r = $el.Current.BoundingRectangle
$x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + [Math]::Min($r.Height / 2, 14))
$target = [C]::Deepest([IntPtr]::new($Hwnd), $x, $y)
[C]::Click($target, $x, $y)
"Bấm '$($el.Current.Name.Split("`n")[0])' tại $x,$y (hwnd $target)"
