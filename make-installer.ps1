# Builds the installer for the ASE (ARK: Survival Evolved) manager: the program files of a build packed into one setup file.
#   dist\ASEARKServerManager-Setup-Release.exe   from  src\ARKServerManager\bin\Release
#   powershell -ExecutionPolicy Bypass -File make-installer.ps1              (builds the manager in Release first)
#   add -SkipBuild to use the build folder as it is
#   add -BumpRevision to raise the version number first (needed for each new release; the manager's version is 1.1.0.<revision>)
#   It also makes the two files of the manager's own upgrade button (GitHub release assets):
#     dist\version.txt                     just the version number
#     dist\ASEARKServerManager-Update.zip  the program files
param(
    [switch]$SkipBuild,
    [switch]$BumpRevision
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$managerProject = Join-Path $root 'src\ARKServerManager\ARKServerManager.csproj'
$source = Join-Path $root 'src\ARKServerManager\bin\Release'
$installerProject = Join-Path $root 'src\ServerManager.Installer\ServerManager.Installer.csproj'
$payloadDir = Join-Path $root 'src\ServerManager.Installer\payload'
$payload = Join-Path $payloadDir 'payload.zip'
$dist = Join-Path $root 'dist'
$setupName = 'ASEARKServerManager-Setup-Release.exe'

if ($BumpRevision) {
    $text = Get-Content $managerProject -Raw
    $m = [regex]::Match($text, '<ApplicationRevision>(\d+)</ApplicationRevision>')
    if (-not $m.Success) { throw 'ApplicationRevision was not found in the manager project.' }
    $next = [int]$m.Groups[1].Value + 1
    $text = $text.Replace($m.Value, "<ApplicationRevision>$next</ApplicationRevision>")
    [System.IO.File]::WriteAllText($managerProject, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "Version raised: revision $next"
}

if (-not $SkipBuild) {
    Write-Host 'Building the manager (Release)...'
    & $msbuild $managerProject -nologo -v:q -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw 'The manager did not build.' }
}

if (-not (Test-Path (Join-Path $source 'ARK Server Manager.exe'))) { throw "No manager build in $source" }

Write-Host 'Packing the program files...'
New-Item -ItemType Directory -Force $payloadDir | Out-Null
if (Test-Path $payload) { Remove-Item $payload -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    $count = 0
    Get-ChildItem $source -Recurse -File | Where-Object {
        # no debug symbols, and nothing that belongs to a user (settings, data, logs)
        $_.Extension -ne '.pdb' -and
        $_.Name -notlike 'userconfig*' -and $_.Name -notlike 'commonconfig*' -and $_.Extension -ne '.log'
    } | ForEach-Object {
        $relative = $_.FullName.Substring($source.Length).TrimStart('\') -replace '[\\]', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $relative, 'Optimal')
        $count++
    }
    # the updater cannot replace itself while it runs: the new one travels as NewServerManagerUpdater.exe, and the manager swaps it in at the next upgrade
    $newUpdater = Join-Path $source 'ServerManagerUpdater.exe'
    if (Test-Path $newUpdater) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $newUpdater, 'NewServerManagerUpdater.exe', 'Optimal')
        $count++
    }
    Write-Host "  $count files"
} finally {
    $zip.Dispose()
}
Write-Host ("  payload {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

Write-Host 'Building the installer...'
& $msbuild $installerProject -nologo -v:q -restore -p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw 'The installer did not build.' }

$built = Join-Path $root 'src\ServerManager.Installer\bin\Release\net462\ASEARKServerManager-Setup.exe'
if (-not (Test-Path $built)) { throw "Installer not found at $built" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $built (Join-Path $dist $setupName) -Force
# the manager's own version is the one in its manifest (what the program reports and compares)
$manifest = Join-Path $source 'ARK Server Manager.exe.manifest'
$version = ([regex]::Match((Get-Content $manifest -Raw), 'assemblyIdentity name="ARK Server Manager.exe" version="([^"]+)"')).Groups[1].Value
if (-not $version) { throw 'The version was not found in the manifest.' }
[System.IO.File]::WriteAllText((Join-Path $dist 'version.txt'), $version)
Copy-Item $payload (Join-Path $dist 'ASEARKServerManager-Update.zip') -Force
Write-Host "Update files for GitHub: version.txt ($version) and ASEARKServerManager-Update.zip"
Write-Host ("Done: {0} ({1:N1} MB)" -f (Join-Path $dist $setupName), ((Get-Item (Join-Path $dist $setupName)).Length / 1MB))
