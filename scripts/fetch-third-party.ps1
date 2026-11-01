# Downloads the native libraries ClipClip bundles into third_party. Run once after cloning.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) "clipclip-third-party-$PID"
New-Item -ItemType Directory -Force $temp | Out-Null

$ffmpegDir = Join-Path $root 'third_party\ffmpeg'
$modelDir = Join-Path $root 'third_party\vosk-model'
New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
New-Item -ItemType Directory -Force $modelDir | Out-Null

Invoke-WebRequest 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip' -OutFile "$temp\ffmpeg.zip"
Expand-Archive "$temp\ffmpeg.zip" "$temp\ffmpeg"
$bin = (Get-ChildItem "$temp\ffmpeg" -Recurse -Filter 'avcodec-*.dll' | Select-Object -First 1).DirectoryName
foreach ($name in 'avcodec-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll') {
    Copy-Item (Join-Path $bin $name) $ffmpegDir -Force
}
Get-ChildItem "$temp\ffmpeg" -Recurse -Filter 'LICENSE*' | Select-Object -First 1 | Copy-Item -Destination "$ffmpegDir\LICENSE.txt" -Force

Invoke-WebRequest 'https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip' -OutFile "$temp\model.zip"
Expand-Archive "$temp\model.zip" "$temp\model"
$model = Get-ChildItem "$temp\model" -Directory | Select-Object -First 1
Copy-Item "$($model.FullName)\*" $modelDir -Recurse -Force

Remove-Item $temp -Recurse -Force
Write-Host 'third_party is ready.'
