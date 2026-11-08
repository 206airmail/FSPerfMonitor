# build.ps1 - build SVS Performance Monitor (needs the .NET 10 SDK). -Publish also writes ONE native SVSPerfMonitor.exe to publish\ (NativeAOT, ~8 MB,
# no .NET install needed on the target machine; needs the Visual Studio C++ build tools for the native link).
#   -FetchPresentMon  downloads Intel's PresentMon console (MIT, signed by Intel) to tools\ for full mode; pinned + hash-checked
param([switch]$Publish, [switch]$FetchPresentMon)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src\FSPerf.csproj'
$pmVersion = '2.6.0'
$pmSha256 = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
$pmFile = Join-Path $PSScriptRoot "tools\PresentMon-$pmVersion-x64.exe"
if ($FetchPresentMon -and -not (Test-Path $pmFile)) {
    New-Item -ItemType Directory -Force (Split-Path $pmFile) | Out-Null
    Invoke-WebRequest "https://github.com/GameTechDev/PresentMon/releases/download/v$pmVersion/PresentMon-$pmVersion-x64.exe" -OutFile $pmFile -UseBasicParsing
    if ((Get-FileHash $pmFile -Algorithm SHA256).Hash -ne $pmSha256) { Remove-Item $pmFile; throw "PresentMon $pmVersion hash mismatch" }
    Write-Host "PresentMon $pmVersion -> $pmFile"
}
dotnet build $src -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    # the native linker step locates the C++ tools with vswhere, which the VS installer doesn't put on PATH
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
    dotnet publish $src -c Release -nologo -v q -o (Join-Path $PSScriptRoot 'publish')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (Test-Path $pmFile) { Copy-Item $pmFile (Join-Path $PSScriptRoot 'publish') }  # full mode finds it next to SVSPerfMonitor.exe
}
Write-Host "FSPERF BUILD OK"
