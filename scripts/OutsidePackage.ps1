# Chạy script bên ngoài ứng dụng đóng gói MSIX.
#
# Khi PowerShell được mở từ một ứng dụng MSIX (ví dụ Claude Desktop / Claude Code trong Claude Desktop),
# Windows có thể chuyển hướng file ghi vào %LOCALAPPDATA% sang vùng riêng của ứng dụng đó
# (%LOCALAPPDATA%\Packages\<gói>\LocalCache\Local\...): Revit và AI client khác không thấy file vừa cài.
# Hàm dưới đây phát hiện tình huống này và nhờ Explorer chạy lại script ở ngoài.

# Phát hiện bằng thực nghiệm (API GetCurrentPackageFullName không phản ánh đúng trường hợp này).
# Quy luật đo được: thư mục MỚI tạo ngay dưới %LOCALAPPDATA% bị chuyển hướng, còn ghi vào thư mục thật đã có thì không.
# Nên thử tạo một thư mục mới ở cấp đó rồi xem nó có rơi vào vùng riêng của gói nào không.
function Test-InPackage {
    $name = "VnRevitMcpProbe-" + [guid]::NewGuid().ToString("N")
    $probe = Join-Path $env:LOCALAPPDATA $name
    New-Item -ItemType Directory $probe | Out-Null
    try {
        return [bool](Get-ChildItem (Join-Path $env:LOCALAPPDATA "Packages\*\LocalCache\Local\$name") -Force -ErrorAction SilentlyContinue)
    }
    finally {
        Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Chạy lại $Script (kèm tham số) qua Explorer, chờ xong và in log.
function Invoke-OutsidePackage([string]$Script, [string[]]$Arguments) {
    $root = Split-Path $PSScriptRoot -Parent
    $work = Join-Path $root "artifacts"
    New-Item -ItemType Directory -Force $work | Out-Null
    $id = [guid]::NewGuid().ToString("N").Substring(0, 8)
    $cmd = Join-Path $work "outside-$id.cmd"
    $log = Join-Path $work "outside-$id.log"

    $argText = ($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    @(
        "@echo off",
        "chcp 65001 >nul",
        "powershell -NoProfile -ExecutionPolicy Bypass -File `"$Script`" $argText > `"$log`" 2>&1",
        "echo __VN_REVIT_MCP_DONE__ >> `"$log`""
    ) | Set-Content $cmd -Encoding ASCII

    Write-Host "Đang chạy trong ứng dụng đóng gói (MSIX): chạy lại bên ngoài qua Explorer..." -ForegroundColor Yellow
    Start-Process explorer.exe -ArgumentList "`"$cmd`""
    $deadline = (Get-Date).AddMinutes(15)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern '__VN_REVIT_MCP_DONE__' -Quiet)) { break }
    }
    if (Test-Path $log) {
        Get-Content $log -Encoding UTF8 | Where-Object { $_ -notmatch '__VN_REVIT_MCP_DONE__' } | ForEach-Object { Write-Host $_ }
    } else {
        Write-Warning "Không thấy kết quả. Hãy chạy trực tiếp trong PowerShell: $Script $argText"
    }
    Remove-Item $cmd, $log -ErrorAction SilentlyContinue
}
