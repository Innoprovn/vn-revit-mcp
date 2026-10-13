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

# ---- Gói .mcpb cho Claude Desktop (Settings → Extensions → Install Extension) ----
# Cùng nội dung bản zip, thêm manifest.json và icon.png. Server chạy với "--bundle <thư mục gói>" nên lần đầu
# tự cài add-in Revit mang theo (xem src\InnoPro.Mcp.Server\Setup\BundleInstaller.cs).
$manifest = [ordered]@{
    manifest_version = "0.3"
    name             = "vn-revit-mcp"
    display_name     = "vn-revit-mcp (Revit)"
    version          = $version
    description      = "Cho AI điều khiển Autodesk Revit 2025+: truy vấn, QA-QC, dựng hình từ CAD, cốt thép, bản vẽ."
    long_description = "Cài extension này là đủ: lần chạy đầu tự cài add-in InnoPro vào Revit 2025/2026 (không cần quyền admin). Mở lại Revit, vào tab InnoPro rồi hỏi Claude về mô hình đang mở."
    author           = [ordered]@{ name = "InnoPro"; url = "https://innopro.vn" }
    repository       = [ordered]@{ type = "git"; url = "https://github.com/Innoprovn/vn-revit-mcp" }
    homepage         = "https://github.com/Innoprovn/vn-revit-mcp"
    documentation    = "https://github.com/Innoprovn/vn-revit-mcp/blob/main/docs/huong-dan-su-dung.md"
    support          = "https://github.com/Innoprovn/vn-revit-mcp/issues"
    icon             = "icon.png"
    license          = "GPL-3.0-or-later"
    keywords         = @("revit", "bim", "autodesk", "cad", "rebar", "vietnam")
    server           = [ordered]@{
        type        = "binary"
        entry_point = "server/vn-revit-mcp.exe"
        mcp_config  = [ordered]@{ command = '${__dirname}/server/vn-revit-mcp.exe'; args = @("--bundle", '${__dirname}'); env = @{} }
    }
    tools_generated   = $true
    prompts_generated = $true
    compatibility     = [ordered]@{ platforms = @("win32") }
}
[IO.File]::WriteAllText((Join-Path $stage "manifest.json"), ($manifest | ConvertTo-Json -Depth 6), $ascii)

# Icon 512x512: khối lập phương nét trắng trên nền xanh InnoPro (cùng bộ icon vector của ribbon).
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$visual = New-Object System.Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()
$brand = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x0B, 0x5C, 0xAD))
$dc.DrawRoundedRectangle($brand, $null, (New-Object System.Windows.Rect 0, 0, 512, 512), 96, 96)
$cube = [System.Windows.Media.Geometry]::Parse("M12 3l8.5 4.5v9L12 21l-8.5-4.5v-9z M3.5 7.5L12 12l8.5-4.5 M12 12v9").Clone()
$cube.Transform = New-Object System.Windows.Media.MatrixTransform 15, 0, 0, 15, 76, 76
$pen = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), 28
$pen.StartLineCap = $pen.EndLineCap = $pen.LineJoin = "Round"
$dc.DrawGeometry($null, $pen, $cube)
$dc.Close()
$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap 512, 512, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$png = [IO.File]::Create((Join-Path $stage "icon.png")); try { $encoder.Save($png) } finally { $png.Dispose() }

$mcpb = Join-Path $release "vn-revit-mcp-$version.mcpb"
if (Test-Path $mcpb) { Remove-Item $mcpb -Force }
$archive = [IO.Compression.ZipFile]::Open($mcpb, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -notin "Cai-dat.cmd", "HUONG-DAN.txt" }) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Host "Đã đóng gói: $mcpb ($([math]::Round((Get-Item $mcpb).Length / 1MB, 1)) MB)" -ForegroundColor Green

# ---- server.json cho MCP Registry: đồng bộ version, link và SHA-256 của gói .mcpb ----
# Workflow publish-mcp-registry.yml đăng file này khi bản phát hành được tạo, nên commit nó trước khi tạo release.
$serverJsonPath = Join-Path $root "server.json"
$serverJson = Get-Content $serverJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$serverJson.version = $version
$package = $serverJson.packages | Where-Object { $_.registryType -eq "mcpb" } | Select-Object -First 1
$package.identifier = "https://github.com/Innoprovn/vn-revit-mcp/releases/download/v$version/vn-revit-mcp-$version.mcpb"
$package.fileSha256 = (Get-FileHash $mcpb -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($serverJsonPath, ($serverJson | ConvertTo-Json -Depth 8), $ascii)
Write-Host "server.json: $version, sha256 $($package.fileSha256.Substring(0, 12))..." -ForegroundColor Green
