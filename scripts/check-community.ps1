#Requires -Version 5.1
<#
.SYNOPSIS
  Kiểm tra cửa hàng lệnh cộng đồng: index.json hợp lệ, mỗi mục trỏ tới file có thật, id trùng tên lệnh,
  và mọi file .cs biên dịch được với InnoPro.Revit.Sdk (giống cách Revit biên dịch lúc chạy).
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$community = Join-Path $root "community"

dotnet build (Join-Path $root "src\InnoPro.Revit.Sdk\InnoPro.Revit.Sdk.csproj") -c Release -v q
if ($LASTEXITCODE) { throw "Build SDK thất bại." }
$sdk = Join-Path $root "src\InnoPro.Revit.Sdk\bin\Release\net8.0-windows\InnoPro.Revit.Sdk.dll"

$index = Get-Content (Join-Path $community "index.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$problems = @()
$ids = @{}
foreach ($c in $index.commands) {
    foreach ($field in "id", "file", "title", "description", "category", "author") {
        if (-not $c.$field) { $problems += "Mục '$($c.id)': thiếu '$field'." }
    }
    if ($ids.ContainsKey($c.id)) { $problems += "Trùng id '$($c.id)'." }
    $ids[$c.id] = $true
    $file = Join-Path $community $c.file
    if (-not (Test-Path $file)) { $problems += "Mục '$($c.id)': không có file $($c.file)."; continue }
    $text = Get-Content $file -Raw -Encoding UTF8
    if ($text -notmatch "Name\s*=>\s*`"$([regex]::Escape($c.id))`"") { $problems += "Mục '$($c.id)': id không trùng Name trong $($c.file)." }
    if ($text -match '(?i)HttpClient|WebClient|Process\.Start|System\.Net\b') { $problems += "Mục '$($c.id)': không được gọi mạng hay chạy chương trình khác." }
}

# Biên dịch tất cả file .cs trong một project tạm
$work = Join-Path $root "artifacts\community-check"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><PlatformTarget>x64</PlatformTarget>
    <ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><LangVersion>latest</LangVersion></PropertyGroup>
  <ItemGroup>
    <Compile Include="$community\scripts\*.cs" />
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="2025.4.60" ExcludeAssets="runtime" />
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="2025.4.60" ExcludeAssets="runtime" />
    <Reference Include="$sdk" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $work "check.csproj") -Encoding UTF8
dotnet build (Join-Path $work "check.csproj") -v q
if ($LASTEXITCODE) { $problems += "Có file .cs không biên dịch được (xem lỗi ở trên)." }

if ($problems) { $problems | ForEach-Object { Write-Host "✗ $_" -ForegroundColor Red }; exit 1 }
Write-Host "✓ Cửa hàng lệnh hợp lệ: $($index.commands.Count) lệnh." -ForegroundColor Green
