# ============================================================================
#  DSH Balance Pet - build script (development helper, English only on purpose)
#
#  Produces dist\ : a folder that runs on any Windows 10 1903+ / Windows 11 with
#  nothing installed, because the target is .NET Framework 4.8 (an OS component).
#
#  Usage:
#     powershell -ExecutionPolicy Bypass -File .\build.ps1            # Release
#     powershell -ExecutionPolicy Bypass -File .\build.ps1 -DebugBuild
#     powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test      # + unit tests
#     powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy    # + install here
#
#  -Deploy copies the app next to the artwork and credentials, which is where the
#  launcher expects it: this folder IS the app folder, src\ is the source.
#
#  (-DebugBuild, not -Debug: [CmdletBinding()] already owns -Debug.)
# ============================================================================
[CmdletBinding()]
param(
    [switch]$DebugBuild,
    [switch]$Test,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$config = if ($DebugBuild) { 'Debug' } else { 'Release' }

Write-Host "== build ($config) ==" -ForegroundColor Cyan
dotnet build (Join-Path $root 'DshPet.sln') -c $config --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($Test) {
    Write-Host "== unit tests ==" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'DshPet.sln') -c $config --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

$out = Join-Path $root "src\DshPet.App\bin\$config\net48"
$dist = Join-Path $root 'dist'
Write-Host "== assemble dist ==" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

# Framework-dependent net48 build: the exe, its own assemblies, and the handful
# of BCL facades System.Text.Json needs on .NET Framework. No runtime to install
# (.NET Framework 4.8 is part of Windows), so this folder just runs.
Get-ChildItem $out -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.config' } |
    Copy-Item -Destination $dist

foreach ($asset in 'sprite.png', 'hit.mp3') {
    $src = Join-Path $root $asset
    if (Test-Path $src) { Copy-Item $src $dist }
}
Copy-Item (Join-Path $root '启动DSH余额宠物.vbs') $dist -ErrorAction SilentlyContinue

Get-ChildItem $dist | Select-Object Name, @{n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } |
    Format-Table -AutoSize
$total = [math]::Round((Get-ChildItem $dist -File | Measure-Object Length -Sum).Sum / 1KB, 0)
Write-Host "dist ready: $dist  (${total} KB, runs as-is on Windows 10 1903+ / 11)" -ForegroundColor Green
Write-Host "  run it:      $dist\DshPet.exe"
Write-Host "  test modes:  --goprobe | --gosim | --simchain | --shot | --shotgo | --uicheck | --selftest"
Write-Host "  NOTE a GUI exe gets no stdout from PowerShell's '>'; use cmd or Start-Process:" -ForegroundColor Yellow
Write-Host "       cmd /c `"$dist\DshPet.exe --gosim > out.txt`""

if ($Deploy) {
    Write-Host "== install into the app folder ==" -ForegroundColor Cyan
    # The launcher runs the exe from here, next to sprite.png, state.ini and the
    # credential files - so the widget keeps reading exactly the files it always
    # did, and the frozen pwsh_version\dsh_pet.ps1 stays as the fallback.
    Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.exe', '.dll', '.config' } |
        Copy-Item -Destination $root -Force
    Write-Host "deployed to $root" -ForegroundColor Green
    Write-Host "  the launcher will now start DshPet.exe; delete it to fall back to pwsh_version\dsh_pet.ps1"
}
