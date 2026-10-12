#Requires -Version 5.1
<#
.SYNOPSIS
  Vòng phát triển nhanh: build, chép CommandSet vào bản đã cài, rồi bảo Revit đang chạy nạp lại. Không cần tắt Revit.

.DESCRIPTION
  Chỉ áp dụng cho CommandSet (Commands\*), chép vào %LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\addin\Commands.
  Nếu phần lõi (Addin, Sdk, Bridge) thay đổi thì script báo, khi đó chạy install-dev.ps1
  (Revit đang mở thì bản mới có hiệu lực từ lần mở Revit sau).
#>

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$staged = Join-Path $root "artifacts\addin\InnoPro.VnRevitMcp"
$installed = Join-Path $env:LOCALAPPDATA "InnoPro\vn-revit-mcp\current\addin"

if (-not (Test-Path (Join-Path $installed "InnoPro.Revit.Addin.dll"))) {
    throw "Chưa cài vn-revit-mcp tại $installed. Chạy install-dev.ps1 trước."
}

& (Join-Path $PSScriptRoot "build.ps1")

. (Join-Path $PSScriptRoot "HostHash.ps1")
$hostHash = Get-HostSourceHash $root

$hashFile = Join-Path $installed "host.hash"
$hostChanged = -not (Test-Path $hashFile) -or (Get-Content $hashFile -Raw).Trim() -ne $hostHash

# CommandSet được nạp từ bộ nhớ nên không khóa file, chép đè được cả khi Revit đang chạy.
$commands = Join-Path $installed "Commands"
if (Test-Path $commands) { Remove-Item $commands -Recurse -Force }
Copy-Item (Join-Path $staged "Commands") $commands -Recurse
Write-Host "Đã chép CommandSet vào $commands"

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
    Write-Warning "Phần lõi (Addin/Sdk/Bridge) đã thay đổi: chạy install-dev.ps1 rồi mở lại Revit để cập nhật."
}
