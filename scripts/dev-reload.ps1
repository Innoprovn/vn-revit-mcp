#Requires -Version 5.1
<#
.SYNOPSIS
  Vòng phát triển nhanh: build, chép CommandSet vào bản đã cài, rồi bảo Revit đang chạy nạp lại. Không cần tắt Revit.

.DESCRIPTION
  Chỉ áp dụng cho CommandSet (Commands\*). Nếu phần lõi (Addin, Sdk, Bridge) thay đổi thì script báo,
  khi đó phải đóng Revit và chạy install-dev.ps1.
#>
param([int[]]$RevitVersions = @(2025, 2026, 2027))

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$staged = Join-Path $root "artifacts\addin\InnoPro.VnRevitMcp"

& (Join-Path $PSScriptRoot "build.ps1")

$hostChanged = $false
foreach ($year in $RevitVersions) {
    $installed = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\InnoPro.VnRevitMcp"
    if (-not (Test-Path $installed)) { continue }

    foreach ($dll in Get-ChildItem $staged -Filter "InnoPro.*.dll") {
        $target = Join-Path $installed $dll.Name
        if (-not (Test-Path $target) -or (Get-FileHash $dll.FullName).Hash -ne (Get-FileHash $target).Hash) { $hostChanged = $true }
    }

    $commands = Join-Path $installed "Commands"
    if (Test-Path $commands) { Remove-Item $commands -Recurse -Force }
    Copy-Item (Join-Path $staged "Commands") $commands -Recurse
    Write-Host "Đã chép CommandSet vào Revit $year"
}

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
    Write-Warning "Phần lõi (Addin/Sdk/Bridge) đã thay đổi: đóng Revit rồi chạy install-dev.ps1 để cập nhật."
}
