#Requires -Version 5.1
<#
.SYNOPSIS
  Phát hành một lệnh: đóng gói, cập nhật server.json, commit, tạo GitHub release (zip + .mcpb).
  Workflow publish-mcp-registry.yml tự đăng bản mới lên MCP Registry khi release được tạo.

.EXAMPLE
  scripts\release.ps1 -Notes docs\ghi-chu-0.4.0.md
  (phiên bản lấy từ <Version> trong Directory.Build.props; tăng số trước khi chạy)
#>
param(
    [Parameter(Mandatory)][string]$Notes,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "Cần GitHub CLI (gh)." }
if (-not (Test-Path $Notes)) { throw "Không thấy file ghi chú: $Notes" }

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
Push-Location $root
try {
    if (& $gh release view $tag 2>$null) { throw "Đã có release $tag. Tăng <Version> trong Directory.Build.props trước." }
    $dirty = git status --porcelain -- . ':!server.json' ':!artifacts'
    if ($dirty) { throw "Còn thay đổi chưa commit:`n$dirty" }

    & (Join-Path $PSScriptRoot "package.ps1") -SkipBuild:$SkipBuild

    git add server.json Directory.Build.props
    if (git diff --cached --name-only) { git commit -q -m "Phát hành $version"; }
    git pull --rebase -q
    git push -q

    $release = Join-Path $root "artifacts\release"
    & $gh release create $tag (Join-Path $release "vn-revit-mcp-$version-win-x64.zip") (Join-Path $release "vn-revit-mcp-$version.mcpb") `
        --title "vn-revit-mcp $version" --notes-file $Notes --target main
    Write-Host "Đã phát hành $tag. MCP Registry cập nhật qua GitHub Actions trong ít phút." -ForegroundColor Green
}
finally { Pop-Location }
