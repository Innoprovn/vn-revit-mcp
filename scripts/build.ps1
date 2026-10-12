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
New-Item -ItemType Directory -Force $addin | Out-Null

$addinBin = Join-Path $root "src\InnoPro.Revit.Addin\bin\$Configuration\net8.0-windows"
# Gồm cả Roslyn (Microsoft.CodeAnalysis*.dll) để biên dịch file lệnh .cs và nút ribbon.
Copy-Item (Join-Path $addinBin "*.dll"), (Join-Path $addinBin "InnoPro.*.pdb") $addin
Copy-Item (Join-Path $addinBin "InnoPro.VnRevitMcp.addin") (Join-Path $out "addin")

# Module chat: nạp trong AssemblyLoadContext riêng (thư viện riêng, ví dụ SDK Anthropic và System.Text.Json mới hơn).
$chatBin = Join-Path $root "src\InnoPro.Revit.Chat\bin\$Configuration\net8.0-windows"
$chat = Join-Path $addin "Modules\Chat"
New-Item -ItemType Directory -Force $chat | Out-Null
Copy-Item (Join-Path $chatBin "*.dll") $chat
Copy-Item (Join-Path $chatBin "InnoPro.Revit.Chat.pdb") $chat -ErrorAction SilentlyContinue
Write-Host "Module Chat"

# Mỗi project có commandset.json là một CommandSet: chép cả thư mục output vào Commands\<name>.
foreach ($manifest in Get-ChildItem (Join-Path $root "src") -Recurse -Filter commandset.json | Where-Object { $_.FullName -match '\\bin\\' -and $_.FullName -match "\\$Configuration\\" }) {
    $name = (Get-Content $manifest.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).name
    $target = Join-Path $addin "Commands\$name"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $manifest.DirectoryName "*") $target -Recurse
    # SDK do host cung cấp, không để bản sao trong CommandSet.
    Remove-Item (Join-Path $target "InnoPro.Revit.Sdk.*") -ErrorAction SilentlyContinue
    Write-Host "CommandSet $name"
}

dotnet publish (Join-Path $root "src\InnoPro.Mcp.Server\InnoPro.Mcp.Server.csproj") -c $Configuration -r win-x64 --self-contained false -o (Join-Path $out "server")
if ($LASTEXITCODE -ne 0) { throw "Publish MCP server thất bại." }

Write-Host "Xong: $out" -ForegroundColor Green
