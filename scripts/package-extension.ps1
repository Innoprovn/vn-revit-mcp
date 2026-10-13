#Requires -Version 5.1
<#
.SYNOPSIS
  Đóng gói một tiện ích marketplace (CommandSet) thành zip, tính SHA-256 và in mục danh mục cho community/index.json.

.EXAMPLE
  scripts\package-extension.ps1 -Project extensions\Sim4D -Id sim4d -Category "Mô phỏng" -Publish
  (-Publish: tạo GitHub release "ext-<id>-v<version>" kèm zip và cập nhật community/index.json)
#>
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Id,
    [string]$Category = "Tiện ích",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$projectDir = Resolve-Path (Join-Path $root $Project)
$csproj = Get-ChildItem $projectDir -Filter *.csproj | Select-Object -First 1
$xml = [xml](Get-Content $csproj.FullName -Raw -Encoding UTF8)
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$description = ($xml.Project.PropertyGroup.Description | Where-Object { $_ } | Select-Object -First 1)
$manifest = Get-Content (Join-Path $projectDir "commandset.json") -Raw -Encoding UTF8 | ConvertFrom-Json

dotnet build $csproj.FullName -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build lỗi." }
$bin = Get-ChildItem (Join-Path $projectDir "bin\Release") -Directory | Select-Object -First 1

# Bỏ những gì host đã cung cấp (SDK, Revit API) và file gỡ lỗi.
$stage = Join-Path $root "artifacts\extensions\$Id"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem $bin.FullName -File | Where-Object { $_.Name -notmatch '^(InnoPro\.Revit\.Sdk|RevitAPI|RevitAPIUI)\.' -and $_.Extension -ne ".pdb" } |
    Copy-Item -Destination $stage

$zip = Join-Path $root "artifacts\extensions\$Id-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -File) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$tag = "ext-$Id-v$version"
$commands = Select-String -Path (Join-Path $projectDir "*.cs") -Pattern 'Name => "([a-z0-9_]+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
$entry = [ordered]@{
    id = $Id; name = $manifest.name; description = $description; category = $Category; author = $manifest.author
    version = $version; url = "https://github.com/Innoprovn/vn-revit-mcp/releases/download/$tag/$(Split-Path $zip -Leaf)"
    sha256 = $sha; size_kb = [int][math]::Ceiling((Get-Item $zip).Length / 1KB); commands = @($commands)
}
Write-Host "Đã đóng gói: $zip ($($entry.size_kb) KB)" -ForegroundColor Green

if ($Publish) {
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
    & $gh release create $tag $zip --title "$($manifest.name) $version (tiện ích)" --notes "Tiện ích marketplace vn-revit-mcp. Cài trong Revit: Cửa hàng, hoặc nhờ AI: marketplace_install id=$Id." --target main
    $indexPath = Join-Path $root "community\index.json"
    $index = Get-Content $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $list = @($index.extensions | Where-Object { $_ -and $_.id -ne $Id }) + [pscustomobject]$entry
    $index | Add-Member -NotePropertyName extensions -NotePropertyValue $list -Force
    [IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Đã phát hành $tag và cập nhật community/index.json (nhớ commit + push)." -ForegroundColor Green
}
else {
    $entry | ConvertTo-Json -Depth 4
}
