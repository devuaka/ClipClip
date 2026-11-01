# Builds the self-contained release and the installer into artifacts/release.
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
$release = Join-Path $root 'artifacts\release'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root 'src\ClipClip') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true "-p:Version=$Version" -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Push-Location $root
try {
    dotnet tool restore
    dotnet vpk pack --packId ClipClip --packVersion $Version --packDir $publish --mainExe ClipClip.exe `
        --packTitle ClipClip --packAuthors ClipClip --icon (Join-Path $root 'src\ClipClip\Assets\ClipClip.ico') `
        --outputDir $release
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
}
finally {
    Pop-Location
}

# The site in docs serves these two files from its downloads folder.
$siteDownloads = Join-Path $root 'docs\downloads'
New-Item -ItemType Directory -Force $siteDownloads | Out-Null
Copy-Item (Join-Path $release 'ClipClip-win-Setup.exe'), (Join-Path $release 'ClipClip-win-Portable.zip') $siteDownloads -Force

Get-ChildItem $release | ForEach-Object { '{0,-42} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
