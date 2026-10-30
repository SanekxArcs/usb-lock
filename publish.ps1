$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'UsbLock/UsbLock.csproj'
$outputPath = Join-Path $PSScriptRoot 'dist/USB-Lock-0.2.0'
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Ready: $outputPath/UsbLock.exe"

