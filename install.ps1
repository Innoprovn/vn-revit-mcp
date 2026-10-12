# vn-revit-mcp: one-line installer bootstrap.
#
#   irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex
#
# This file is intentionally ASCII-only, without a BOM and without a param() block, so it runs cleanly
# through "irm | iex" on Windows PowerShell 5.1. It downloads the full installer (Install-VnRevitMcp.ps1,
# Vietnamese messages, UTF-8 with BOM) and runs it as a normal script file.

& {
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $url = 'https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/Install-VnRevitMcp.ps1'
    $file = Join-Path $env:TEMP ('vn-revit-mcp-install-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.ps1')
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $file
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $file
    }
    catch {
        Write-Warning ('vn-revit-mcp: ' + $_.Exception.Message)
    }
    finally {
        Remove-Item $file -ErrorAction SilentlyContinue
    }
}
