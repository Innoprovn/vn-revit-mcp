# Ngoại lệ plugin cho vn-revit-mcp / Plugin exception

vn-revit-mcp được phát hành theo **GNU General Public License phiên bản 3** (xem [LICENSE](LICENSE)),
kèm các quyền bổ sung dưới đây theo Điều 7 của GPLv3.

> Bản tiếng Anh bên dưới là bản có giá trị pháp lý. Bản tiếng Việt chỉ để dễ hiểu.

## Tóm tắt (tiếng Việt)

1. **CommandSet riêng được giữ license riêng.** Bạn có thể viết CommandSet (thư viện chỉ giao tiếp với
   vn-revit-mcp qua các interface công khai trong `InnoPro.Revit.Sdk`), phân phối nó theo bất kỳ điều khoản
   nào bạn chọn, kể cả đóng mã nguồn, và để vn-revit-mcp nạp nó lúc chạy. Bản thân vn-revit-mcp
   và mọi sửa đổi đối với nó vẫn theo GPLv3.
2. **Chạy cùng Autodesk Revit.** Được phép liên kết và chạy vn-revit-mcp với Autodesk Revit và các
   thư viện API của Revit (RevitAPI.dll, RevitAPIUI.dll...), dù các thành phần đó không theo GPL.

## Additional permissions under GNU GPL version 3 section 7

Copyright (C) 2026 InnoPro

**1. Command Set exception.**
A "Command Set" means a separately distributed library that (a) interacts with this Program solely
through the public interfaces and types of the `InnoPro.Revit.Sdk` assembly, (b) is loaded by this
Program at run time through its command-set loading mechanism, and (c) does not contain any part of
this Program other than declarations needed to use those interfaces.

As a special exception, the copyright holders of this Program give you permission to combine this
Program with Command Sets, and to convey the resulting combination, without the Command Sets being
subject to the terms of the GNU General Public License. The Corresponding Source of such a combination
need not include the source code of the Command Sets, which you may license under terms of your choice.
This exception does not apply to modifications of this Program itself, which remain subject to the
GNU General Public License version 3.

**2. Autodesk Revit exception.**
As a special exception, you have permission to link this Program with, and run it inside, Autodesk
Revit and its application programming interface libraries (including RevitAPI.dll and RevitAPIUI.dll),
and to convey the resulting work, even though those components are not licensed under the GNU General
Public License. Their source code need not be included in the Corresponding Source.

If you modify this Program, you may extend these exceptions to your version, but you are not obliged
to do so. If you do not wish to do so, delete this exception statement from your version.
