# Đăng ký ký số miễn phí SignPath Foundation

Việc người đại diện InnoPro làm (một lần), khoảng 15 phút:

1. Mở https://signpath.org/apply và điền đơn bằng nội dung dưới đây.
2. Chờ SignPath Foundation duyệt (thường vài ngày đến vài tuần). Họ tạo tổ chức và project trên app.signpath.io, gửi thông tin đăng nhập.
3. Trên app.signpath.io:
   - Project `vn-revit-mcp`, nối với repo GitHub `Innoprovn/vn-revit-mcp` (Trusted Build System: GitHub.com).
   - Signing policy `release-signing`, artifact configuration `initial` = nội dung XML ở cuối file này.
   - Tạo API token (CI user) có quyền submit cho project.
4. Trên GitHub repo → Settings → Secrets and variables → Actions:
   - Secret `SIGNPATH_API_TOKEN` = token ở bước 3.
   - Variable `SIGNPATH_ORGANIZATION_ID` = Organization ID trên SignPath.
   - (Tùy chọn) `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`, `SIGNPATH_ARTIFACT_CONFIG` nếu đặt tên khác mặc định.

Xong bước 4, mỗi lần phát hành `v*` workflow `sign-release.yml` tự build, gửi ký, thay gói trong release và đăng MCP Registry.
Mỗi yêu cầu ký cần người có quyền Approver bấm duyệt trên SignPath (hoặc email).

## Nội dung đơn (tiếng Anh)

- **Project name:** vn-revit-mcp
- **Repository:** https://github.com/Innoprovn/vn-revit-mcp
- **Homepage:** https://github.com/Innoprovn/vn-revit-mcp
- **License:** GPL-3.0 with a plugin exception (OSI-approved GPLv3)
- **Description:** Open-source MCP server and Autodesk Revit 2025+ add-in that lets AI assistants (Claude, Cursor, VS Code Copilot,
  Gemini, local models...) read and model Revit projects through 100+ commands, with an in-Revit chat, self-update and hot reload.
  Used by architects and structural engineers in Vietnam; released for Windows as a zip, a one-line PowerShell installer and an MCP bundle.
- **What will be signed:** our own .NET assemblies (Revit add-in loader/core, command sets, MCP server exe). Built by GitHub Actions
  from tagged sources.
- **Why signing:** Revit warns about unsigned add-ins ("publisher could not be verified") on first load; signing lets users verify
  the publisher.
- **Code signing policy:** https://github.com/Innoprovn/vn-revit-mcp/blob/main/docs/code-signing-policy.md
- **Team:** InnoPro maintainers (contact: innopro@innopro.vn)

## Artifact configuration (dán vào SignPath)

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <directory path="**">
      <pe-file path="InnoPro.*.dll" max-matches="unbounded">
        <authenticode-sign />
      </pe-file>
      <pe-file path="vn-revit-mcp.*" max-matches="unbounded">
        <authenticode-sign />
      </pe-file>
    </directory>
  </zip-file>
</artifact-configuration>
```
