# Builds GameOp and produces both release flavours in .\dist:
#   GameOp-Portable-v<ver>.exe   single file; run from anywhere, settings stay in .\GameOp-Data next to it
#   GameOp-Setup-v<ver>.exe      installer (Program Files, Start menu + optional desktop shortcut)
param([string]$Version = "1.1.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

Remove-Item -Recurse -Force publish, dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory dist | Out-Null

& $dotnet publish GameOp.csproj -c Release -o publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Remove-Item publish\*.pdb -ErrorAction SilentlyContinue

# Portable: the same single-file exe; "Portable" in the name switches on portable mode.
Copy-Item publish\GameOp.exe "dist\GameOp-Portable-v$Version.exe"

# Installer
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=..\publish" installer\GameOp.iss
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
} else {
    Write-Warning "Inno Setup 6 not found - skipped the installer."
}

Get-ChildItem dist | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
