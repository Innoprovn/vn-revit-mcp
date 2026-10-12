#Requires -Version 5.1
<#
.SYNOPSIS
  Gỡ vn-revit-mcp: xóa file .addin, gỡ khỏi AI client, xóa chương trình.

.DESCRIPTION
  Mặc định giữ lại dữ liệu người dùng trong %LOCALAPPDATA%\InnoPro\vn-revit-mcp
  (Commands\, Scripts\, logs\, commands.json). Thêm -Purge để xóa hết.

    powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-Purge]
#>
param([switch]$Purge)

function Uninstall-VnRevitMcp {
    param([switch]$Purge)

    $ErrorActionPreference = "Stop"
    $root = Join-Path $env:LOCALAPPDATA "InnoPro\vn-revit-mcp"
    $current = Join-Path $root "current"
    $problems = @()

    if (Get-Process Revit -ErrorAction SilentlyContinue) {
        Write-Warning "Revit đang mở: một số file đang được dùng có thể chưa xóa được. Nên đóng Revit rồi chạy lại."
    }

    # 1. Gỡ khỏi AI client (cần exe nên làm trước khi xóa chương trình)
    $exe = Join-Path $current "server\vn-revit-mcp.exe"
    if (Test-Path $exe) {
        Write-Host "-> Gỡ khỏi AI client" -ForegroundColor Cyan
        & $exe unsetup
        if ($LASTEXITCODE -ne 0) { $problems += "Gỡ khỏi một số AI client không thành công (xem thông báo ở trên)." }
    }
    else { $problems += "Không thấy $exe nên chưa gỡ được khỏi AI client." }

    # 2. File .addin và bố cục cũ của install-dev.ps1
    foreach ($year in 2025..2030) {
        $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
        $file = Join-Path $addins "InnoPro.VnRevitMcp.addin"
        if (Test-Path $file) { Remove-Item $file -Force; Write-Host "Đã xóa $file" }
        $oldDev = Join-Path $addins "InnoPro.VnRevitMcp"
        if (Test-Path $oldDev) {
            try { Remove-Item $oldDev -Recurse -Force } catch { $problems += "Chưa xóa được $oldDev." }
        }
    }

    # 3. Chương trình: junction current (chỉ gỡ liên kết) và app\
    $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { cmd /c rmdir "$current" | Out-Null }
    $apps = Join-Path $root "app"
    if (Test-Path $apps) {
        try { Remove-Item $apps -Recurse -Force } catch { $problems += "Chưa xóa hết $apps (file đang được dùng)." }
    }

    if ($Purge -and (Test-Path $root)) {
        try { Remove-Item $root -Recurse -Force } catch { $problems += "Chưa xóa hết $root." }
    }

    Write-Host ""
    if ($problems.Count -eq 0) { Write-Host "Đã gỡ vn-revit-mcp." -ForegroundColor Green }
    else {
        Write-Host "Đã gỡ vn-revit-mcp, còn vướng:" -ForegroundColor Yellow
        $problems | ForEach-Object { Write-Host "  - $_" }
    }
    if (-not $Purge -and (Test-Path $root)) {
        Write-Host "Giữ lại dữ liệu người dùng tại $root (thêm -Purge để xóa)."
    }
}

$scriptFile = if ($MyInvocation.MyCommand.Name -eq "uninstall.ps1") { $MyInvocation.MyCommand.Path }
try { Uninstall-VnRevitMcp -Purge:$Purge }
catch {
    Write-Host "Gỡ cài đặt thất bại: $($_.Exception.Message)" -ForegroundColor Red
    if ($scriptFile) { exit 1 }
}
