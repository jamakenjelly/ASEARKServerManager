# Builds the installer for the ASE (ARK: Survival Evolved) manager: the program files of a build packed into one setup file.
#   dist\ASEARKServerManager-Setup-Release.exe   from  src\ARKServerManager\bin\Release
#   powershell -ExecutionPolicy Bypass -File make-installer.ps1              (builds the manager in Release first)
#   add -SkipBuild to use the build folder as it is
param(
    [switch]$SkipBuild
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
Write-Host ("Done: {0} ({1:N1} MB)" -f (Join-Path $dist $setupName), ((Get-Item (Join-Path $dist $setupName)).Length / 1MB))
