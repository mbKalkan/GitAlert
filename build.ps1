<#
.SYNOPSIS
    Builds, tests and packages GitAlert.

.DESCRIPTION
    One entry point for everything CI and a local release need:

        .\build.ps1                     # restore, build, test
        .\build.ps1 -Publish            # + a self-contained win-x64 folder in artifacts/publish
        .\build.ps1 -Installer          # + GitAlert-Setup-<version>-x64.exe in artifacts/
        .\build.ps1 -Installer -Version 1.2.0

    The installer needs Inno Setup 6. Install it with `winget install JRSoftware.InnoSetup`
    or `choco install innosetup`; the script finds ISCC.exe on PATH or in Program Files.

.PARAMETER Version
    Overrides the version stamped into the assemblies and the installer. Defaults to the
    VersionPrefix in Directory.Build.props.

.PARAMETER FrameworkDependent
    Publishes against an installed .NET 9 Desktop Runtime instead of bundling it. Produces a
    far smaller output, but the machine must already have the runtime.
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $Publish,
    [switch] $Installer,
    [switch] $Zip,
    [switch] $SkipTests,
    [switch] $FrameworkDependent,
    [switch] $RegenerateIcon
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'src/GitAlert/GitAlert.csproj'
$tests = Join-Path $root 'tests/GitAlert.Tests/GitAlert.Tests.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$iconPath = Join-Path $root 'src/GitAlert/Resources/app.ico'

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Invoke-Checked([string] $what, [scriptblock] $action) {
    & $action
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed with exit code $LASTEXITCODE."
    }
}

function Get-ProductVersion {
    if ($Version) { return $Version }

    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<VersionPrefix>([^<]+)</VersionPrefix>') { return $Matches[1] }

    return '1.0.0'
}

function Find-InnoSetup {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

$productVersion = Get-ProductVersion
Write-Host "GitAlert $productVersion ($Configuration)" -ForegroundColor Green

# The .NET version fields want four parts; the product version stays semantic.
$assemblyVersion = "$productVersion.0"
$versionArgs = @(
    "-p:Version=$productVersion",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$productVersion"
)

Write-Step 'Restoring'
Invoke-Checked 'restore' { dotnet restore (Join-Path $root 'GitAlert.sln') }

Write-Step 'Building'
Invoke-Checked 'build' {
    dotnet build (Join-Path $root 'GitAlert.sln') -c $Configuration --no-restore @versionArgs
}

if (-not $SkipTests) {
    Write-Step 'Testing'
    Invoke-Checked 'tests' {
        dotnet test $tests -c $Configuration --no-build --verbosity quiet --nologo
    }
}

if ($RegenerateIcon) {
    # The application icon is drawn by the app itself, so the .ico and the on-screen mark
    # always come from the same vector artwork.
    Write-Step 'Regenerating app.ico from the vector artwork'

    $builtExe = Join-Path $root "src/GitAlert/bin/$Configuration/net9.0-windows/GitAlert.exe"
    if (-not (Test-Path $builtExe)) {
        $builtExe = Join-Path $root "src/GitAlert/bin/$Configuration/net9.0-windows/win-x64/GitAlert.exe"
    }

    # GitAlert is a GUI subsystem executable, so the call operator would not wait for it.
    $export = Start-Process -FilePath $builtExe -ArgumentList '--export-icon', $iconPath -Wait -PassThru -NoNewWindow
    if ($export.ExitCode -ne 0) { throw "Icon export failed with exit code $($export.ExitCode)." }
    if (-not (Test-Path $iconPath)) { throw "Icon export did not write $iconPath." }

    Write-Host "    wrote $iconPath"
}

if (-not ($Publish -or $Installer -or $Zip)) {
    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green
    return
}

Write-Step 'Publishing win-x64'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()

Invoke-Checked 'publish' {
    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained $selfContained `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=none `
        @versionArgs `
        -o $publishDir
}

$exe = Join-Path $publishDir 'GitAlert.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }

$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    $publishDir ($sizeMb MB)"

if ($Zip) {
    Write-Step 'Packing the portable archive'

    $zipPath = Join-Path $artifacts "GitAlert-$productVersion-win-x64-portable.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
    Write-Host "    $zipPath"
}

if ($Installer) {
    Write-Step 'Building the installer'

    $iscc = Find-InnoSetup
    if (-not $iscc) {
        throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
    }

    $script = Join-Path $root 'installer/GitAlert.iss'

    Invoke-Checked 'ISCC' {
        & $iscc `
            "/DAppVersion=$productVersion" `
            "/DPublishDir=$publishDir" `
            "/DOutputDir=$artifacts" `
            $script
    }

    $setup = Join-Path $artifacts "GitAlert-Setup-$productVersion-x64.exe"
    if (Test-Path $setup) {
        $setupMb = [math]::Round(((Get-Item $setup).Length / 1MB), 1)
        Write-Host "    $setup ($setupMb MB)"
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
