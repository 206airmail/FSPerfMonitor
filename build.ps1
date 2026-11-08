# build.ps1 - build SVS Performance Monitor (needs the .NET 10 SDK).
#   -Publish  the release files in publish\ (no .NET install needed on the target machine; needs the Visual Studio C++
#             build tools for the native link and the starter):
#               SVSPerfMonitor.exe  the starter players double-click (tiny GUI program: no console window, any Windows)
#               SVSPerfMonitor.com  the program (NativeAOT, ~8 MB); `SVSPerfMonitor` in a terminal finds the .com first
#               PresentMon-<ver>-x64.exe  when tools\ has it (frame times)
#   -FetchPresentMon  downloads Intel's PresentMon console (MIT, signed by Intel) to tools\ for full mode; pinned + hash-checked
#   -Out <dir>  publish there instead of publish\ (e.g. while a copy from publish\ is running; publish_test\ is ignored by git)
param([switch]$Publish, [switch]$FetchPresentMon, [string]$Out)
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'   # the SDK's first-run notice goes to stderr, which this script would treat as an error
$src = Join-Path $PSScriptRoot 'src\FSPerf.csproj'
$pub = if ($Out) { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Out)) } else { Join-Path $PSScriptRoot 'publish' }
$pmVersion = '2.6.0'
$pmSha256 = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
$pmFile = Join-Path $PSScriptRoot "tools\PresentMon-$pmVersion-x64.exe"
# the hash is checked EVERY time, not only after a download: a damaged or replaced file in tools\ never ships
if ((Test-Path $pmFile) -and (Get-FileHash $pmFile -Algorithm SHA256).Hash -ne $pmSha256) {
    if (-not $FetchPresentMon) { throw "$pmFile does not match PresentMon $pmVersion (SHA256): delete it, or build with -FetchPresentMon" }
    Remove-Item $pmFile   # re-downloaded below
}
if ($FetchPresentMon -and -not (Test-Path $pmFile)) {
    New-Item -ItemType Directory -Force (Split-Path $pmFile) | Out-Null
    Invoke-WebRequest "https://github.com/GameTechDev/PresentMon/releases/download/v$pmVersion/PresentMon-$pmVersion-x64.exe" -OutFile $pmFile -UseBasicParsing
    if ((Get-FileHash $pmFile -Algorithm SHA256).Hash -ne $pmSha256) { Remove-Item $pmFile; throw "PresentMon $pmVersion hash mismatch" }
    Write-Host "PresentMon $pmVersion -> $pmFile"
}
if ($Publish) {
    # the native linker step locates the C++ tools with vswhere, which the VS installer doesn't put on PATH
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
    New-Item -ItemType Directory -Force $pub | Out-Null
    Remove-Item (Join-Path $pub 'SVSPerfMonitor.*') -ErrorAction SilentlyContinue
    dotnet publish $src -c Release -nologo -v q -o $pub
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # the program becomes SVSPerfMonitor.com (Windows runs a file by its contents, not its extension)
    Move-Item (Join-Path $pub 'SVSPerfMonitor.exe') (Join-Path $pub 'SVSPerfMonitor.com') -Force
    Move-Item (Join-Path $pub 'SVSPerfMonitor.pdb') (Join-Path $pub 'SVSPerfMonitor.com.pdb') -Force -ErrorAction SilentlyContinue

    # the starter, SVSPerfMonitor.exe: C, no C runtime; version from the csproj
    $ver = ([xml](Get-Content $src)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $p = @($ver.Split('.') + @('0', '0', '0'))[0..3]
    $obj = Join-Path $PSScriptRoot 'src\obj\starter'
    New-Item -ItemType Directory -Force $obj | Out-Null
    Set-Content (Join-Path $obj 'starter_ver.h') -Encoding ascii -Value @(
        "#define SVS_VER_COMMA $($p -join ',')", "#define SVS_VER_STR `"$ver`"")
    $vs = & vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $vcvars)) { throw "Visual Studio C++ build tools not found (vcvars64.bat)" }
    $dir = Join-Path $PSScriptRoot 'src\starter'
    $res = Join-Path $obj 'starter.res'
    $out = Join-Path $pub 'SVSPerfMonitor.exe'
    $bat = Join-Path $obj 'build_starter.cmd'
    Set-Content $bat -Encoding ascii -Value @(
        '@echo off',
        "call `"$vcvars`" >nul || exit /b 1",
        "cd /d `"$dir`"",
        "rc /nologo /I `"$obj`" /fo `"$res`" starter.rc || exit /b 1",
        "cl /nologo /O1 /GS- /W4 /WX /Fo`"$obj\\`" starter.c /link /SUBSYSTEM:WINDOWS /ENTRY:Start /NODEFAULTLIB kernel32.lib user32.lib `"$res`" /OUT:`"$out`" || exit /b 1")
    & cmd.exe /c "`"$bat`""
    if ($LASTEXITCODE -ne 0) { throw "starter build failed" }
    if (Test-Path $pmFile) { Copy-Item $pmFile $pub }  # frame times find it next to the program
}
# the regular build last: a native publish reuses src\bin and leaves no SVSPerfMonitor.exe there (the self-test's default)
dotnet build $src -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "SVSPERFMONITOR BUILD OK"
