# Fetches the build tools this repo needs into tools/ (not committed):
#   Reclaimer 2.1.1644 (GPL-3.0, Gravemind2401) - the Halo map reader the converter links against
#   .NET 9 SDK - builds the converter
$t = Join-Path $PSScriptRoot "..\tools"
New-Item -ItemType Directory -Force $t | Out-Null
Invoke-WebRequest https://github.com/Gravemind2401/Reclaimer/releases/download/v2.1.1644/Reclaimer.Setup-v2.1.1644.msi -OutFile "$t\Reclaimer.msi"
Start-Process msiexec -ArgumentList "/a `"$t\Reclaimer.msi`" /qn TARGETDIR=`"$t\reclaimer`"" -Wait
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$t\dotnet-install.ps1"
& "$t\dotnet-install.ps1" -Channel 9.0 -InstallDir "$t\dotnet9" -NoPath
