#Requires -Version 5.1
<#
.SYNOPSIS
  Build và đóng gói bản phát hành artifacts\release\vn-revit-mcp-<phiên bản>-win-x64.zip.

.DESCRIPTION
  Trong zip:
    addin\          add-in + CommandSet đi kèm
    server\         vn-revit-mcp.exe (framework-dependent, cần .NET 8 có sẵn cùng Revit 2025+)
    Install-VnRevitMcp.ps1, uninstall.ps1, Cai-dat.cmd, HUONG-DAN.txt, version.txt
#>
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") }

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Không đọc được <Version> trong Directory.Build.props." }
$name = "vn-revit-mcp-$version-win-x64"

$release = Join-Path $artifacts "release"
$stage = Join-Path $release $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "addin"), (Join-Path $stage "server") | Out-Null

Copy-Item (Join-Path $artifacts "addin\InnoPro.VnRevitMcp\*") (Join-Path $stage "addin") -Recurse
Copy-Item (Join-Path $artifacts "server\*") (Join-Path $stage "server") -Recurse
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root "Install-VnRevitMcp.ps1"), (Join-Path $root "uninstall.ps1") $stage

$ascii = New-Object Text.UTF8Encoding($false)
$utf8Bom = New-Object Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $stage "version.txt"), $version, $ascii)

# Cai-dat.cmd chỉ dùng ký tự ASCII để cmd đọc đúng. "%~dp0." tránh dấu \ cuối làm hỏng dấu ngoặc kép.
$cmd = @"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-VnRevitMcp.ps1" -Source "%~dp0."
echo.
pause
"@
[IO.File]::WriteAllText((Join-Path $stage "Cai-dat.cmd"), ($cmd -replace "`r?`n", "`r`n"), $ascii)

$guide = @"
vn-revit-mcp $version - Cho AI điều khiển Autodesk Revit (2025 trở lên)
https://github.com/Innoprovn/vn-revit-mcp

CÀI ĐẶT
  1. Giải nén file zip ra một thư mục bất kỳ.
  2. Bấm đúp Cai-dat.cmd. Không cần quyền admin, không cần đóng Revit
     (nếu Revit đang mở thì bản mới có hiệu lực từ lần mở Revit sau).
  3. Bộ cài tự kết nối các AI client có trên máy: Claude Desktop, Claude Code, Cursor,
     VS Code (GitHub Copilot), Windsurf, Gemini CLI, LM Studio, Codex CLI, Cline.
  4. Mở Revit, vào tab InnoPro, rồi hỏi AI: "Mô hình Revit đang mở có bao nhiêu phần tử?"

AI CLIENT KHÁC
  Khai báo MCP server kiểu stdio với command:
    %LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe
  Cài AI client sau khi cài vn-revit-mcp thì chạy lại lệnh trên kèm tham số "setup".

GỠ CÀI ĐẶT
  powershell -ExecutionPolicy Bypass -File uninstall.ps1
  (thêm -Purge để xóa cả CommandSet và script bạn tự thêm)

Mã nguồn mở theo GPLv3, do InnoPro (https://innopro.vn) phát triển.
"@
[IO.File]::WriteAllText((Join-Path $stage "HUONG-DAN.txt"), ($guide -replace "`r?`n", "`r`n"), $utf8Bom)

$zip = Join-Path $release "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Tự ghi từng entry với dấu "/" (ZipFile.CreateFromDirectory của PowerShell 5.1 ghi "\", sai chuẩn zip).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

Write-Host "Đã đóng gói: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
