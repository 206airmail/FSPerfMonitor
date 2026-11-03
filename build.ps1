# build.ps1 - build FSPerf (needs the .NET 8 SDK). -Publish also writes a self-contained single fsperf.exe to publish\
# (no .NET install needed on the target machine).
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src\FSPerf.csproj'
dotnet build $src -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    dotnet publish $src -c Release -nologo -v q --self-contained true -p:PublishSingleFile=true -o (Join-Path $PSScriptRoot 'publish')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host "FSPERF BUILD OK"
