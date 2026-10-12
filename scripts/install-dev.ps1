#Requires -Version 5.1
<#
.SYNOPSIS
  Cài bản build trên máy dev vào các phiên bản Revit có trên máy (2025 trở lên).

.DESCRIPTION
  - Add-in:     %APPDATA%\Autodesk\Revit\Addins\<năm>\InnoPro.VnRevitMcp
  - MCP server: %LOCALAPPDATA%\InnoPro\vn-revit-mcp\server\vn-revit-mcp.exe
  Revit phải đóng trước khi cài (Revit khóa file DLL đang nạp).
#>
param(
    [int[]]$RevitVersions = @(2025, 2026, 2027),
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw "Revit đang chạy. Hãy lưu công việc, đóng Revit rồi chạy lại script."
}

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") }

. (Join-Path $PSScriptRoot "HostHash.ps1")
$hostHash = Get-HostSourceHash $root

$installed = @()
foreach ($year in $RevitVersions) {
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $year\Revit.exe")) { continue }

    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $target = Join-Path $addins "InnoPro.VnRevitMcp"
    New-Item -ItemType Directory -Force $addins | Out-Null
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item (Join-Path $artifacts "addin\InnoPro.VnRevitMcp") $target -Recurse
    Copy-Item (Join-Path $artifacts "addin\InnoPro.VnRevitMcp.addin") $addins -Force
    Set-Content (Join-Path $target "host.hash") $hostHash -Encoding ASCII
    $installed += $year
}
if ($installed.Count -eq 0) { throw "Không tìm thấy Revit 2025 trở lên trên máy." }

$server = Join-Path $env:LOCALAPPDATA "InnoPro\vn-revit-mcp\server"
New-Item -ItemType Directory -Force $server | Out-Null
Copy-Item (Join-Path $artifacts "server\*") $server -Recurse -Force
$exe = Join-Path $server "vn-revit-mcp.exe"

Write-Host ""
Write-Host "Đã cài add-in cho Revit: $($installed -join ', ')" -ForegroundColor Green
Write-Host "MCP server: $exe"
Write-Host ""
Write-Host "Kết nối với Claude Code:"
Write-Host "  claude mcp add vn-revit-mcp --scope user -- `"$exe`""
Write-Host ""
Write-Host "Client khác (Claude Desktop, Cursor, VS Code...) dùng cấu hình stdio với command:"
Write-Host "  $exe"
