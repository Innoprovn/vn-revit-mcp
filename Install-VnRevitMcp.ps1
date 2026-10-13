#Requires -Version 5.1
<#
.SYNOPSIS
  Cài vn-revit-mcp (add-in Revit + MCP server) và tự kết nối các AI client trên máy. Không cần quyền admin.

.DESCRIPTION
  Cài một dòng (PowerShell):
    irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex

  Truyền tham số khi cài một dòng:
    (chọn phiên bản) tải Install-VnRevitMcp.ps1 rồi chạy: -File Install-VnRevitMcp.ps1 -Version 0.1.0

  Cài từ file zip hoặc thư mục đã giải nén:
    powershell -ExecutionPolicy Bypass -File Install-VnRevitMcp.ps1 -Source <zip hoặc thư mục>

  Bố cục sau khi cài:
    %LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\<phiên bản>\addin\    add-in và CommandSet đi kèm
    %LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\<phiên bản>\server\   vn-revit-mcp.exe
    %LOCALAPPDATA%\InnoPro\vn-revit-mcp\current                   junction trỏ tới phiên bản đang dùng
    %APPDATA%\Autodesk\Revit\Addins\<năm>\InnoPro.VnRevitMcp.addin

.PARAMETER Source
  File zip hoặc thư mục bản phát hành (có addin\ và server\). Bỏ trống thì tải từ GitHub.
.PARAMETER Version
  Phiên bản cần tải (mặc định: mới nhất). Khi có -Source thì dùng làm tên phiên bản cài (ví dụ "dev").
.PARAMETER NoClients
  Không tự kết nối AI client.
#>
param(
    [string]$Source,
    [string]$Version,
    [switch]$NoClients
)

function Install-VnRevitMcp {
    param([string]$Source, [string]$Version, [switch]$NoClients)

    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"
    $repo = "Innoprovn/vn-revit-mcp"
    $root = Join-Path $env:LOCALAPPDATA "InnoPro\vn-revit-mcp"
    $addinFileName = "InnoPro.VnRevitMcp.addin"

    function Write-Step([string]$Text) { Write-Host "-> $Text" -ForegroundColor Cyan }

    # Thư mục bị khóa khi Revit hoặc AI client đang dùng DLL/exe trong đó.
    function Test-FolderInUse([string]$Path) {
        if (-not (Test-Path $Path)) { return $false }
        foreach ($file in Get-ChildItem $Path -Recurse -File -Include *.dll, *.exe -ErrorAction SilentlyContinue) {
            try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
            catch { return $true }
        }
        return $false
    }

    function Test-Junction([string]$Path) {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        return $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
    }

    # Tìm thư mục chứa addin\ và server\. Hỗ trợ cả bố cục artifacts của build.ps1 (addin\InnoPro.VnRevitMcp\).
    function Find-Layout([string]$Dir) {
        $candidates = @($Dir) + @(Get-ChildItem $Dir -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)
        foreach ($c in $candidates) {
            $server = Join-Path $c "server\vn-revit-mcp.exe"
            if (-not (Test-Path $server)) { continue }
            foreach ($addin in @((Join-Path $c "addin"), (Join-Path $c "addin\InnoPro.VnRevitMcp"))) {
                if (Test-Path (Join-Path $addin "InnoPro.Revit.Addin.dll")) {
                    return [pscustomobject]@{ Root = $c; Addin = $addin; Server = (Join-Path $c "server") }
                }
            }
        }
        return $null
    }

    # 1. Kiểm tra máy
    if ($env:OS -ne "Windows_NT") { throw "vn-revit-mcp chỉ chạy trên Windows." }
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $years = @(2025..2030 | Where-Object { Test-Path (Join-Path $programFiles "Autodesk\Revit $_\Revit.exe") })
    if ($years.Count -eq 0) { throw "Không tìm thấy Revit 2025 trở lên trên máy (thư mục $programFiles\Autodesk\Revit <năm>)." }
    $revitRunning = [bool](Get-Process Revit -ErrorAction SilentlyContinue)

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("vn-revit-mcp-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force $temp | Out-Null
    try {
        # 2. Lấy bản phát hành
        $zip = $null
        if ($Source) {
            $Source = (Resolve-Path $Source).Path
            if (Test-Path $Source -PathType Leaf) { $zip = $Source }
        }
        else {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $headers = @{ "User-Agent" = "vn-revit-mcp-installer"; "Accept" = "application/vnd.github+json" }
            if (-not $Version -or $Version -eq "latest") {
                $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
            }
            else {
                $release = $null
                foreach ($tag in @("v$($Version.TrimStart('v'))", $Version)) {
                    try { $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers; break } catch { }
                }
                if (-not $release) { throw "Không tìm thấy phiên bản $Version trên GitHub." }
            }
            $asset = $release.assets | Where-Object { $_.name -like "vn-revit-mcp-*-win-x64.zip" } | Select-Object -First 1
            if (-not $asset) { throw "Bản phát hành $($release.tag_name) không có file vn-revit-mcp-*-win-x64.zip." }
            Write-Step "Tải $($asset.name)"
            $zip = Join-Path $temp $asset.name
            Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ "User-Agent" = "vn-revit-mcp-installer" }
        }

        $folder = $Source
        if ($zip) {
            Write-Step "Giải nén"
            $folder = Join-Path $temp "pkg"
            Expand-Archive -LiteralPath $zip -DestinationPath $folder -Force
        }
        $layout = Find-Layout $folder
        if (-not $layout) { throw "Không thấy addin\InnoPro.Revit.Addin.dll và server\vn-revit-mcp.exe trong $folder." }

        # 3. Xác định phiên bản
        if (-not $Version -or $Version -eq "latest") {
            $versionFile = Join-Path $layout.Root "version.txt"
            if (Test-Path $versionFile) { $Version = (Get-Content $versionFile -Raw).Trim() }
            elseif ($zip -and ([IO.Path]::GetFileName($zip) -match '^vn-revit-mcp-(.+)-win-x64\.zip$')) { $Version = $Matches[1] }
            else { $Version = (Get-Item (Join-Path $layout.Addin "InnoPro.Revit.Addin.dll")).VersionInfo.ProductVersion -replace '\+.*$', '' }
        }
        if (-not $Version) { $Version = "0.0.0" }
        if ($Version -notmatch '^[A-Za-z0-9._-]+$') { throw "Tên phiên bản không hợp lệ: $Version" }

        # 4. Chép vào app\<phiên bản>. Bản cùng tên đang được dùng thì cài sang thư mục mới.
        $apps = Join-Path $root "app"
        New-Item -ItemType Directory -Force $apps | Out-Null
        $folderName = $Version
        $target = Join-Path $apps $folderName
        if (Test-Path $target) {
            if (Test-FolderInUse $target) {
                $folderName = "$Version-" + (Get-Date -Format "yyyyMMddHHmmss")
                $target = Join-Path $apps $folderName
            }
            else { Remove-Item $target -Recurse -Force }
        }
        Write-Step "Cài phiên bản $Version vào $target"
        New-Item -ItemType Directory -Force (Join-Path $target "addin"), (Join-Path $target "server") | Out-Null
        Copy-Item (Join-Path $layout.Addin "*") (Join-Path $target "addin") -Recurse -Force
        Copy-Item (Join-Path $layout.Server "*") (Join-Path $target "server") -Recurse -Force
        Get-ChildItem $target -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

        # 5. Trỏ junction current sang bản mới (được phép cả khi file bản cũ đang mở).
        $current = Join-Path $root "current"
        if (Test-Junction $current) { cmd /c rmdir "$current" | Out-Null }
        elseif (Test-Path $current) { Rename-Item $current ("current.old-" + (Get-Date -Format "yyyyMMddHHmmss")) }
        cmd /c mklink /J "$current" "$target" | Out-Null
        if (-not (Test-Junction $current)) { throw "Không tạo được junction $current." }

        # 6. File .addin cho từng phiên bản Revit
        $dll = Join-Path $current "addin\InnoPro.Revit.Addin.dll"
        $addinXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>vn-revit-mcp</Name>
    <Assembly>$([Security.SecurityElement]::Escape($dll))</Assembly>
    <FullClassName>InnoPro.Revit.Addin.App</FullClassName>
    <ClientId>2C094443-BAE5-4DAD-83DF-1DF8DDDF5C68</ClientId>
    <VendorId>InnoPro</VendorId>
    <VendorDescription>InnoPro, https://github.com/Innoprovn/vn-revit-mcp</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
        foreach ($year in $years) {
            $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
            New-Item -ItemType Directory -Force $addins | Out-Null
            [IO.File]::WriteAllText((Join-Path $addins $addinFileName), $addinXml, (New-Object Text.UTF8Encoding($false)))
            # Bố cục cũ của install-dev.ps1: add-in chép thẳng vào thư mục Addins.
            $oldDev = Join-Path $addins "InnoPro.VnRevitMcp"
            if (Test-Path $oldDev) {
                try { Remove-Item $oldDev -Recurse -Force }
                catch { Write-Warning "Chưa xóa được bản cài cũ $oldDev (Revit đang dùng). Chạy lại bộ cài sau khi đóng Revit để dọn." }
            }
        }

        # 7. Dọn các phiên bản cũ không còn được dùng
        Get-ChildItem $apps -Directory | Where-Object { $_.FullName -ne $target } | ForEach-Object {
            if (-not (Test-FolderInUse $_.FullName)) { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        }

        # 8. Kết nối AI client
        $exe = Join-Path $current "server\vn-revit-mcp.exe"
        $connected = @(); $restart = @(); $setupFailed = $false
        if (-not $NoClients) {
            Write-Step "Kết nối AI client"
            try {
                $psi = New-Object Diagnostics.ProcessStartInfo $exe, "setup --json"
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.RedirectStandardInput = $true
                $psi.CreateNoWindow = $true
                $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
                $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
                $p = [Diagnostics.Process]::Start($psi)
                $p.StandardInput.Close()
                $out = $p.StandardOutput.ReadToEndAsync()
                $err = $p.StandardError.ReadToEndAsync()
                if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "vn-revit-mcp.exe setup chạy quá lâu." }
                $json = $out.Result
                $result = $json | ConvertFrom-Json
                foreach ($c in $result.clients) {
                    if ($c.configured) { $connected += $c.name }
                    if ($c.restart) { $restart += "  - $($c.name): $($c.restart)" }
                    if ($c.action -eq "error") { Write-Warning "$($c.name): $($c.message)"; $setupFailed = $true }
                }
            }
            catch {
                $setupFailed = $true
                Write-Warning "Không tự kết nối được AI client: $_"
                if ($err -and $err.Result) { Write-Warning $err.Result.Trim() }
                Write-Warning "Nếu lỗi do thiếu .NET 8, cài .NET 8 Runtime tại https://dotnet.microsoft.com/download/dotnet/8.0 rồi chạy: `"$exe`" setup"
            }
        }

        # 9. Tóm tắt
        Write-Host ""
        Write-Host "Đã cài vn-revit-mcp $Version cho Revit $($years -join ', ')." -ForegroundColor Green
        if ($revitRunning) {
            Write-Host "Revit đang mở: bản mới có hiệu lực từ lần mở Revit tiếp theo." -ForegroundColor Yellow
        }
        Write-Host "Mở Revit → tab InnoPro."
        if (-not $NoClients) {
            if ($connected.Count -gt 0) { Write-Host "AI đã kết nối: $($connected -join ', ')" -ForegroundColor Green }
            elseif (-not $setupFailed) { Write-Host "Chưa thấy AI client nào trên máy. Cài AI client (Claude, Cursor, VS Code...) rồi chạy: `"$exe`" setup" }
            if ($restart.Count -gt 0) {
                Write-Host "Để AI nhận kết nối mới:"
                $restart | ForEach-Object { Write-Host $_ }
            }
        }
        Write-Host "AI client khác: khai báo MCP server kiểu stdio, command = $exe"
        Write-Host ""
        Write-Host "Kiểm tra (vn-revit-mcp doctor):"
        # Đầu vào rỗng: bản cũ chưa có "doctor" sẽ chạy như MCP server và thoát ngay khi hết stdin, không treo bộ cài.
        try { "" | & $exe doctor | ForEach-Object { Write-Host "  $_" } } catch { Write-Warning "Không chạy được doctor: $($_.Exception.Message)" }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# AI client chạy dạng MSIX (Claude Desktop/Claude Code bản Store...) bị Windows chuyển hướng thư mục mới tạo dưới
# %LOCALAPPDATA% vào vùng riêng của ứng dụng: Revit không thấy add-in. Phát hiện bằng thực nghiệm rồi nhờ Explorer
# chạy lại chính script này ở ngoài, chờ xong và in lại kết quả.
function Test-InPackage {
    $name = "VnRevitMcpProbe-" + [guid]::NewGuid().ToString("N")
    $probe = Join-Path $env:LOCALAPPDATA $name
    New-Item -ItemType Directory $probe -ErrorAction SilentlyContinue | Out-Null
    try { return [bool](Get-ChildItem (Join-Path $env:LOCALAPPDATA "Packages\*\LocalCache\Local\$name") -Force -ErrorAction SilentlyContinue) }
    finally { Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue }
}

function Invoke-Outside([string]$Script, [string[]]$Arguments) {
    $id = [guid]::NewGuid().ToString("N").Substring(0, 8)
    $cmd = Join-Path $env:TEMP "vn-revit-mcp-outside-$id.cmd"
    $log = Join-Path $env:TEMP "vn-revit-mcp-outside-$id.log"
    $argText = ($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    @(
        "@echo off", "chcp 65001 >nul", "set VN_REVIT_MCP_OUTSIDE=1",
        "powershell -NoProfile -ExecutionPolicy Bypass -File `"$Script`" $argText > `"$log`" 2>&1",
        "echo __VN_REVIT_MCP_DONE__ >> `"$log`""
    ) | Set-Content $cmd -Encoding ASCII
    Write-Host "Đang chạy trong ứng dụng đóng gói (MSIX): chạy bộ cài ở ngoài qua Explorer..." -ForegroundColor Yellow
    Start-Process explorer.exe -ArgumentList "`"$cmd`""
    $deadline = (Get-Date).AddMinutes(15)
    while ((Get-Date) -lt $deadline -and -not ((Test-Path $log) -and (Select-String -Path $log -Pattern '__VN_REVIT_MCP_DONE__' -Quiet))) { Start-Sleep -Seconds 2 }
    $ok = $false
    if (Test-Path $log) {
        $lines = Get-Content $log -Encoding UTF8 | Where-Object { $_ -notmatch '__VN_REVIT_MCP_DONE__' }
        $lines | ForEach-Object { Write-Host $_ }
        $ok = -not ($lines -match 'Cài đặt thất bại')
    }
    else { Write-Warning "Không thấy kết quả. Hãy chạy trực tiếp trong PowerShell: $Script $argText" }
    Remove-Item $cmd, $log -ErrorAction SilentlyContinue
    return $ok
}

# Bọc trong hàm để chạy được qua "irm | iex" mà không đóng cửa sổ PowerShell khi lỗi.
# Chỉ dùng exit khi chạy từ file (-File hoặc & Install-VnRevitMcp.ps1); chạy qua iex thì exit sẽ đóng cửa sổ.
# Chạy từ file (kể cả file tạm do install.ps1 tải về) thì có đường dẫn; chạy qua "irm | iex" thì rỗng.
$scriptFile = $PSCommandPath
try {
    if ($scriptFile -and -not $env:VN_REVIT_MCP_OUTSIDE -and (Test-InPackage)) {
        $forward = @()
        if ($Source) { $forward += "-Source", $Source }
        if ($Version) { $forward += "-Version", $Version }
        if ($NoClients) { $forward += "-NoClients" }
        $ok = Invoke-Outside $scriptFile $forward
        exit $(if ($ok) { 0 } else { 1 })
    }
    Install-VnRevitMcp -Source $Source -Version $Version -NoClients:$NoClients
    if ($scriptFile) { exit 0 }
}
catch {
    Write-Host ""
    Write-Host "Cài đặt thất bại: $($_.Exception.Message)" -ForegroundColor Red
    if ($scriptFile) { exit 1 }
}
