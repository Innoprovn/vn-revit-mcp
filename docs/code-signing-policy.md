# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Release builds of vn-revit-mcp published on [GitHub Releases](https://github.com/Innoprovn/vn-revit-mcp/releases) (tags `v*`):

- `InnoPro.Revit.Addin.dll` (Revit add-in loader), `InnoPro.Revit.Core.dll` and other `InnoPro.*.dll` in the add-in and its bundled command sets
- `vn-revit-mcp.exe` / `vn-revit-mcp.dll` (MCP server)

Only files built from this repository are signed. Third-party libraries (for example Roslyn) keep their publishers' signatures.
Binaries are built by GitHub Actions from the tagged source (`.github/workflows/sign-release.yml`), never on a developer machine.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [InnoPro maintainers](https://github.com/orgs/Innoprovn/people) |
| Approvers (approve each signing request) | InnoPro maintainers with owner rights on the GitHub organization |

All changes reach `main` through commits by committers; outside contributions are reviewed in pull requests before merge.

## Privacy policy

vn-revit-mcp runs locally on the user's machine and does not transfer information to any networked system unless the user asks for it:

- **Update check:** the MCP server and the Revit add-in query the GitHub API (`api.github.com/repos/Innoprovn/vn-revit-mcp`) at most every 12 hours to find new releases and download them. No user or model data is sent. Disable with environment variable `VN_REVIT_MCP_AUTO_UPDATE=0`.
- **Marketplace:** installing an extension downloads it from GitHub Releases of this repository.
- **AI chat inside Revit:** messages and Revit data needed for the conversation are sent to the AI provider the user configured (or a local model). Nothing is sent before the user configures a provider and writes a message.

---

# Chính sách ký số (tóm tắt tiếng Việt)

Ký số miễn phí do SignPath.io cung cấp, chứng chỉ của SignPath Foundation. Chỉ ký file do InnoPro build từ mã nguồn trong repo này,
build trên GitHub Actions từ đúng tag phát hành. Phần mềm chạy trên máy người dùng; chỉ kết nối GitHub để kiểm tra/tải bản mới
(tắt bằng `VN_REVIT_MCP_AUTO_UPDATE=0`) và kết nối nhà cung cấp AI do người dùng tự chọn khi chat.
