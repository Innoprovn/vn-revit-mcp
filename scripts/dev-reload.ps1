#Requires -Version 5.1
<#
.SYNOPSIS
  Vòng phát triển nhanh: build, chép CommandSet vào bản đã cài, rồi bảo Revit đang chạy nạp lại. Không cần tắt Revit.

.DESCRIPTION
  Chép CommandSet, lõi (InnoPro.Revit.Core, Sdk, Bridge) và module chat vào %LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\addin.
  Revit đang mở tự nạp nóng lõi mới khi rảnh. Chỉ khi bộ nạp (InnoPro.Revit.Loader) đổi thì script báo chạy install-dev.ps1
  rồi mở lại Revit.
#>

param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$staged = Join-Path $root "artifacts\addin\InnoPro.VnRevitMcp"
$installed = Join-Path $env:LOCALAPPDATA "InnoPro\vn-revit-mcp\current\addin"

if (-not (Test-Path (Join-Path $installed "InnoPro.Revit.Addin.dll"))) {
    throw "Chưa cài vn-revit-mcp tại $installed. Chạy install-dev.ps1 trước."
}

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") }

# Trong ứng dụng MSIX (ví dụ Claude Desktop), ghi vào %LOCALAPPDATA% bị chuyển hướng: chép ở ngoài qua Explorer.
. (Join-Path $PSScriptRoot "OutsidePackage.ps1")
if (Test-InPackage) {
    Invoke-OutsidePackage $PSCommandPath @("-SkipBuild")
    return
}

. (Join-Path $PSScriptRoot "HostHash.ps1")
$hostHash = Get-HostSourceHash $root

$hashFile = Join-Path $installed "host.hash"
$hostChanged = -not (Test-Path $hashFile) -or (Get-Content $hashFile -Raw).Trim() -ne $hostHash

# CommandSet được nạp từ bộ nhớ nên không khóa file, chép đè được cả khi Revit đang chạy.
$commands = Join-Path $installed "Commands"
if (Test-Path $commands) { Remove-Item $commands -Recurse -Force }
Copy-Item (Join-Path $staged "Commands") $commands -Recurse
Write-Host "Đã chép CommandSet vào $commands"

# Lõi, SDK, Bridge, module chat: Revit chạy bản sao trong cache\core nên chép đè được; bộ nạp thấy lõi đổi thì tự nạp
# nóng khi Revit rảnh (vài giây). Bộ nạp (InnoPro.Revit.Addin.dll) đang bị Revit khóa nên không chép.
Get-ChildItem $staged -File | Where-Object { $_.Name -notlike "InnoPro.Revit.Addin.*" } | Copy-Item -Destination $installed -Force
$modules = Join-Path $installed "Modules"
if (Test-Path $modules) { Remove-Item $modules -Recurse -Force }
Copy-Item (Join-Path $staged "Modules") $modules -Recurse
Write-Host "Đã chép lõi và module chat; Revit đang mở tự nạp bản mới khi rảnh."

# Gửi reload_commands tới mọi phiên Revit đang chạy add-in.
$pipes = [IO.Directory]::GetFiles("\\.\pipe\") | ForEach-Object { [IO.Path]::GetFileName($_) } | Where-Object { $_ -like "InnoPro.VnRevitMcp.*" }
foreach ($pipe in $pipes) {
    $client = New-Object IO.Pipes.NamedPipeClientStream(".", $pipe, [IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(3000)
        $utf8 = New-Object Text.UTF8Encoding($false)
        $writer = New-Object IO.StreamWriter($client, $utf8); $writer.AutoFlush = $true
        $reader = New-Object IO.StreamReader($client, $utf8)
        $writer.WriteLine('{"id":"dev","method":"reload_commands"}')
        Write-Host "$pipe -> $($reader.ReadLine())" -ForegroundColor Green
    }
    catch { Write-Warning "$pipe -> $_" }
    finally { $client.Dispose() }
}
if (-not $pipes) { Write-Host "Không có Revit nào đang chạy; lệnh mới sẽ có khi mở Revit." }

if ($hostChanged) {
    Write-Warning "Bộ nạp (InnoPro.Revit.Loader) đã thay đổi: chạy install-dev.ps1 rồi mở lại Revit để cập nhật."
}
