# package.ps1 - the release zip from a published build: dist\SVSPerfMonitor-<version>.zip + its .sha256.
#   build.ps1 -Publish -FetchPresentMon first (or pass -Publish here to do it). Used by the release workflow too.
#   Contents (flat): SVSPerfMonitor.exe (starter), SVSPerfMonitor.com (program), PresentMon-2.6.0-x64.exe,
#   README.txt (players), LICENSE.txt (MIT), PresentMon-LICENSE.txt (Intel, MIT). No .pdb.
param([switch]$Publish, [string]$From = 'publish')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if ($Publish) { & (Join-Path $root 'build.ps1') -Publish -FetchPresentMon; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
$pub = Join-Path $root $From
$version = ([xml](Get-Content (Join-Path $root 'src\FSPerf.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$need = @('SVSPerfMonitor.exe', 'SVSPerfMonitor.com')
foreach ($f in $need) { if (-not (Test-Path (Join-Path $pub $f))) { throw "$f missing in $pub (run build.ps1 -Publish)" } }
$pm = @(Get-ChildItem $pub -Filter 'PresentMon-*.exe')
if ($pm.Count -ne 1) { throw "expected exactly one PresentMon in $pub (build.ps1 -Publish -FetchPresentMon), found $($pm.Count)" }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "SVSPerfMonitor-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($f in $need + $pm.Name) { Copy-Item (Join-Path $pub $f) $stage }
Copy-Item (Join-Path $root 'package\README.txt') $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'third_party\PresentMon-LICENSE.txt') $stage
Copy-Item (Join-Path $root 'third_party\qrcode-generator-LICENSE.txt') $stage   # built into the program (the phone QR code, #18)
# the native program contains .NET runtime code (NativeAOT): its licence and third-party notices ship too, from the
# runtime pack this build used (version from the build's project.assets.json)
$assets = Get-Content (Join-Path $root 'src\obj\project.assets.json') -Raw | ConvertFrom-Json
$aot = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.NativeAOT.win-x64' } | Select-Object -First 1
if (-not $aot) { throw "the NativeAOT runtime pack is not in src\obj\project.assets.json (build.ps1 -Publish first)" }
$aotVersion = $aot.version.Trim('[', ']', ' ').Split(',')[0].Trim()   # "[10.0.14, 10.0.14]"
$aotDir = Join-Path $assets.project.restore.packagesPath "microsoft.netcore.app.runtime.nativeaot.win-x64\$aotVersion"
foreach ($n in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
    $f = Join-Path $aotDir $n
    if (-not (Test-Path $f)) { throw "$n missing in $aotDir" }
    Copy-Item $f (Join-Path $stage (".NET-" + $n.Replace('.TXT', '.txt')))
}

$zip = Join-Path $dist "SVSPerfMonitor-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  SVSPerfMonitor-$version.zip" | Set-Content "$zip.sha256" -Encoding ascii
Get-ChildItem $stage | ForEach-Object { "  {0,-28} {1,10:N0} bytes" -f $_.Name, $_.Length }
"$zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)  SHA256 $hash"
