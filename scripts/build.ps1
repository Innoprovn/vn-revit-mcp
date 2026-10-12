#Requires -Version 5.1
<#
.SYNOPSIS
  Build toàn bộ vn-revit-mcp và gom kết quả vào thư mục artifacts.

.OUTPUTS
  artifacts\addin\InnoPro.VnRevitMcp.addin
  artifacts\addin\InnoPro.VnRevitMcp\...           (add-in + CommandSet đi kèm)
  artifacts\server\vn-revit-mcp.exe                (MCP server)
#>
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"

dotnet build (Join-Path $root "vn-revit-mcp.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build thất bại." }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$addin = Join-Path $out "addin\InnoPro.VnRevitMcp"
$core = Join-Path $addin "Commands\Core"
New-Item -ItemType Directory -Force $addin, $core | Out-Null

$addinBin = Join-Path $root "src\InnoPro.Revit.Addin\bin\$Configuration\net8.0-windows"
Copy-Item (Join-Path $addinBin "InnoPro.*.dll"), (Join-Path $addinBin "InnoPro.*.pdb") $addin
Copy-Item (Join-Path $addinBin "InnoPro.VnRevitMcp.addin") (Join-Path $out "addin")

$coreBin = Join-Path $root "src\InnoPro.Revit.Commands.Core\bin\$Configuration\net8.0-windows"
Copy-Item (Join-Path $coreBin "InnoPro.Revit.Commands.Core.*"), (Join-Path $coreBin "commandset.json") $core

dotnet publish (Join-Path $root "src\InnoPro.Mcp.Server\InnoPro.Mcp.Server.csproj") -c $Configuration -r win-x64 --self-contained false -o (Join-Path $out "server")
if ($LASTEXITCODE -ne 0) { throw "Publish MCP server thất bại." }

Write-Host "Xong: $out" -ForegroundColor Green
