# Băm mã nguồn bộ nạp (InnoPro.Revit.Loader): phần duy nhất phải mở lại Revit khi đổi; lõi, SDK, Bridge được nạp nóng.
# Không băm DLL vì .NET gắn mã commit vào DLL, làm DLL đổi sau mỗi commit dù mã không đổi.
function Get-HostSourceHash([string]$Root) {
    $files = Get-ChildItem (Join-Path $Root "src") -Recurse -File -Include *.cs, *.csproj, *.addin |
        Where-Object { $_.FullName -match '\\src\\InnoPro\.Revit\.Loader\\' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object FullName
    $text = ($files | ForEach-Object { $_.FullName.Substring($Root.Length) + ":" + (Get-FileHash $_.FullName).Hash }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString("x2") })
}
