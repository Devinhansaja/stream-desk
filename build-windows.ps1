# Builds the Windows host into windows\publish (self-contained, single file) and, if Inno Setup is installed,
# the installer into dist\.   Requires: .NET 8 SDK.   Optional: Inno Setup 6 (iscc.exe).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location "$root\windows\StreamDeckHost"
try {
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o "$root\windows\publish"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
} finally { Pop-Location }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
Copy-Item "$root\windows\publish\StreamDeckHost.exe" "$root\dist\StreamDeckHost.exe" -Force
$h = (Get-FileHash "$root\dist\StreamDeckHost.exe" -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$root\dist\StreamDeckHost.exe.sha256", "$h  StreamDeckHost.exe`n")
Write-Host "Update asset: dist\StreamDeckHost.exe (+ .sha256)"
Compress-Archive -Force -Path "$root\windows\publish\StreamDeckHost.exe" -DestinationPath "$root\dist\StreamDeckHost-portable-win-x64.zip"
Write-Host "Portable build: dist\StreamDeckHost-portable-win-x64.zip"

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (Test-Path $iscc) {
    & $iscc "$root\installer\StreamDeckHost.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Installer: dist\StreamDeckHost-Setup-1.2.3.exe"
} else {
    Write-Warning "Inno Setup not found - installer skipped. Install it (winget install JRSoftware.InnoSetup) and re-run."
}
