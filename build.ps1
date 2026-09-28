# Builds GameOp and produces both release flavours in .\dist:
#   GameOp-Portable-v<ver>.zip   (unzip anywhere; settings stay in .\UserData)
#   GameOp-Setup-v<ver>.exe      (installer, Start menu + optional desktop shortcut)
param([string]$Version = "1.0.0")
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

# Portable
$portable = "dist\GameOp-Portable-v$Version"
New-Item -ItemType Directory $portable | Out-Null
Copy-Item publish\* $portable -Recurse
Copy-Item README.md $portable
Set-Content "$portable\portable.txt" "This file makes GameOp keep its settings and backups in the UserData folder next to GameOp.exe."
Compress-Archive -Path "$portable\*" -DestinationPath "dist\GameOp-Portable-v$Version.zip" -CompressionLevel Optimal
Remove-Item -Recurse -Force $portable

# Installer
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=..\publish" installer\GameOp.iss
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
} else {
    Write-Warning "Inno Setup 6 not found - skipped the installer."
}

Get-ChildItem dist | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
