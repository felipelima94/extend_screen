# Baixa o Android platform-tools oficial (adb.exe) para tools\platform-tools.
# O ExtendedScreen.Host também acha o adb em %LOCALAPPDATA%\Android\Sdk\platform-tools e no PATH,
# então este script só é necessário se você não tiver o Android SDK instalado.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
$zip = Join-Path $env:TEMP "platform-tools-latest-windows.zip"

New-Item -ItemType Directory -Force $tools | Out-Null
Write-Host "Baixando platform-tools do Google..."
Invoke-WebRequest "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $zip
if (Test-Path (Join-Path $tools "platform-tools")) { Remove-Item -Recurse -Force (Join-Path $tools "platform-tools") }
Expand-Archive $zip -DestinationPath $tools -Force
Remove-Item $zip
Write-Host "adb instalado em $(Join-Path $tools 'platform-tools\adb.exe')"
