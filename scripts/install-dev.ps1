#Requires -Version 5.1
<#
.SYNOPSIS
  Build rồi cài bản đang phát triển, dùng đúng bố cục và bộ cài như người dùng (phiên bản "dev").

.DESCRIPTION
  - Chương trình: %LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\dev\ (junction current trỏ tới đây)
  - Add-in:       %APPDATA%\Autodesk\Revit\Addins\<năm>\InnoPro.VnRevitMcp.addin
  - MCP server:   %LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe
  Nếu Revit đang mở, bản mới được cài sang thư mục khác và có hiệu lực từ lần mở Revit sau.
  Sửa CommandSet thì dùng dev-reload.ps1, không cần đóng Revit.
#>
param(
    [switch]$SkipBuild,
    [switch]$NoClients
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") }

# host.hash: dev-reload.ps1 so sánh để biết phần lõi (Addin/Sdk/Bridge) có đổi so với bản đã cài không.
# Ghi vào bản build, install.ps1 chép theo sang app\dev\addin\host.hash.
. (Join-Path $PSScriptRoot "HostHash.ps1")
$staged = Join-Path $artifacts "addin\InnoPro.VnRevitMcp"
Set-Content (Join-Path $staged "host.hash") (Get-HostSourceHash $root) -Encoding ASCII

& (Join-Path $root "install.ps1") -Source $artifacts -Version dev -NoClients:$NoClients
if ($LASTEXITCODE) { throw "Cài đặt thất bại." }
