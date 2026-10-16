#requires -Version 5.1
<#
.SYNOPSIS
    Builds SlimCode Desktop in Release for every target OS and packages
    portable distribution archives.

.DESCRIPTION
    Reads {version} from the <Version> element of SlimCodeDesktop.csproj and
    produces, per runtime identifier, two portable archives in the dist\ folder:

      slimcode_desktop_v{version}_{rid}.{zip|tar.gz}
          Self-contained: bundles the .NET runtime. Runs on any machine, larger.

      slimcode_desktop_v{version}_{rid}_minimal.{zip|tar.gz}
          Framework-dependent: no runtime bundled. Much smaller, but the target
          machine must have the matching .NET runtime installed.

    Publishing every target here is deliberate, and is the project's guard
    against silent platform drift. Exactly one Platform\Platform.<OS>.cs is
    compiled per build, so the compiler cannot tell you that the *other* one no
    longer builds -- a member added on one side and forgotten on the other stays
    invisible until someone runs that OS. Publishing all targets makes that a
    hard failure here, at the last point before an artifact ships.

    Consequence worth knowing: a broken Linux source file fails the whole
    publish, including the Windows artifacts. That is the intent -- fix it or
    pass -Runtimes win-x64 to deliberately skip it.

    Before anything is published, the unit tests (Tests\SlimCodeDesktop.Tests)
    run once per requested target OS that matches the build host -- tests can
    be cross-compiled but not cross-run. On a Windows host that means the
    Windows tests only; the Linux tests (path semantics differ: case,
    separators, rooting) need a Linux host or CI runner. A failing test aborts
    the publish before any artifact is written.

.PARAMETER Runtimes
    Runtime identifiers to publish. Defaults to win-x64 and linux-x64. Each is
    mapped to the matching $(TargetOS) msbuild property automatically.

.PARAMETER Configuration
    The build configuration. Defaults to Release.

.PARAMETER SkipTests
    Publish without running the unit tests.

.EXAMPLE
    .\publish.ps1

.EXAMPLE
    .\publish.ps1 -Runtimes win-x64
    Windows only -- skips the Linux drift check.
#>
[CmdletBinding()]
param(
    [string[]]$Runtimes = @("win-x64", "linux-x64"),
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# Opt out of the .NET CLI telemetry for this run.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$projectName = "SlimCodeDesktop"
$projectFile = Join-Path $PSScriptRoot "$projectName.csproj"
$testProject = Join-Path $PSScriptRoot "Tests\$projectName.Tests\$projectName.Tests.csproj"
$distDir     = Join-Path $PSScriptRoot "dist"

# The OS this script runs on, in $(TargetOS) terms. $IsLinux only exists in
# PowerShell 6+, so test the Windows marker that 5.1 is guaranteed to have.
if ($env:OS -eq "Windows_NT") { $hostOS = "Windows" } else { $hostOS = "Linux" }

# Maps a RID onto the $(TargetOS) property that selects Platform\Platform.<OS>.cs.
# Unknown RIDs throw rather than silently defaulting -- picking the wrong
# platform sources would produce an artifact that fails at runtime, not build.
function Get-TargetOS {
    param([Parameter(Mandatory)][string]$Rid)

    if ($Rid -like "win-*")   { return "Windows" }
    if ($Rid -like "linux-*") { return "Linux" }
    throw "Unknown runtime identifier '$Rid' -- add it to Get-TargetOS in publish.ps1."
}

# 1. Read <Version> from the csproj (no extra dependencies).
if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}
[xml]$csproj = Get-Content -LiteralPath $projectFile
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not find a <Version> element in $projectFile"
}
$version = $version.Trim()

Write-Host "Project       : $projectName" -ForegroundColor Cyan
Write-Host "Version       : $version"     -ForegroundColor Cyan
Write-Host "Configuration : $Configuration"
Write-Host "Runtimes      : $($Runtimes -join ', ')"
Write-Host ""

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

$artifacts = New-Object System.Collections.Generic.List[string]

# Publishes a folder-based build and archives it into dist\. Throws on failure.
function Invoke-PublishArchive {
    param(
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$TargetOS,
        [Parameter(Mandatory)][bool]$SelfContained,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Suffix,   # e.g. "" or "_minimal"
        [Parameter(Mandatory)][string]$Label
    )

    if ($SelfContained) { $stageName = "publish-selfcontained" } else { $stageName = "publish-minimal" }
    $publishDir = Join-Path $PSScriptRoot "bin\$Configuration\net10.0\$Runtime\$stageName"

    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Write-Host "Publishing $Runtime ($Label)..." -ForegroundColor Cyan
    dotnet publish $projectFile `
        -c $Configuration `
        -r $Runtime `
        -p:TargetOS=$TargetOS `
        --self-contained $($SelfContained.ToString().ToLower()) `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $Runtime ($Label) failed with exit code $LASTEXITCODE"
    }

    # Strip debug symbols. -p:DebugType=none drops our managed .pdb, but native
    # packages (SkiaSharp, HarfBuzzSharp) ship their own .pdb as runtime assets;
    # remove any that remain so they stay out of the distribution.
    Get-ChildItem -LiteralPath $publishDir -Filter *.pdb -Recurse | Remove-Item -Force

    # Ship the license index and texts. The BSD/MIT terms of the bundled
    # components require their notices to accompany binary redistribution.
    Copy-Item -Path (Join-Path $PSScriptRoot "LICENSE*.md") -Destination $publishDir -Force

    # tar.gz for Linux (what the platform expects), zip for Windows.
    if ($TargetOS -eq "Linux") { $ext = "tar.gz" } else { $ext = "zip" }
    $archiveName = "slimcode_desktop_v${version}_${Runtime}${Suffix}.${ext}"
    $archivePath = Join-Path $distDir $archiveName
    if (Test-Path $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Write-Host "Packaging $archiveName ..." -ForegroundColor Cyan
    if ($ext -eq "tar.gz") {
        # bsdtar ships with Windows 10 1803+. Fall back to a zip if absent --
        # the exec bit is lost either way (see the note printed at the end), so
        # the format is a convention, not a capability.
        if (Get-Command tar -ErrorAction SilentlyContinue) {
            tar -czf $archivePath -C $publishDir .
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed for $archiveName with exit code $LASTEXITCODE"
            }
        }
        else {
            Write-Host "  tar not found -- falling back to .zip" -ForegroundColor Yellow
            $archiveName = "slimcode_desktop_v${version}_${Runtime}${Suffix}.zip"
            $archivePath = Join-Path $distDir $archiveName
            Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $archivePath -Force
        }
    }
    else {
        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $archivePath -Force
    }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1)
    Write-Host "  -> $archivePath ($sizeMb MB)" -ForegroundColor Green
    Write-Host ""

    $script:artifacts.Add("$archiveName  ($sizeMb MB)")
}

# Runs the unit tests once per distinct target OS the host can execute.
# Throws on the first failure so no artifact is produced from a red build.
function Invoke-UnitTests {
    $targetOses = $Runtimes | ForEach-Object { Get-TargetOS -Rid $_ } | Select-Object -Unique

    foreach ($os in $targetOses) {
        if ($os -ne $hostOS) {
            Write-Host "Skipping $os unit tests -- cannot run on a $hostOS host." -ForegroundColor Yellow
            continue
        }

        Write-Host "=== unit tests  (TargetOS=$os) ===" -ForegroundColor Magenta
        dotnet test $testProject `
            -c $Configuration `
            -p:TargetOS=$os
        if ($LASTEXITCODE -ne 0) {
            throw "Unit tests ($os) failed with exit code $LASTEXITCODE -- nothing was published."
        }
        Write-Host ""
    }
}

if ($SkipTests) {
    Write-Host "Unit tests skipped (-SkipTests)." -ForegroundColor Yellow
    Write-Host ""
}
else {
    Invoke-UnitTests
}

$anyLinux = $false
foreach ($rid in $Runtimes) {
    $targetOs = Get-TargetOS -Rid $rid
    if ($targetOs -eq "Linux") { $anyLinux = $true }

    Write-Host "=== $rid  (TargetOS=$targetOs) ===" -ForegroundColor Magenta

    # Self-contained: bundles the .NET runtime, runs anywhere.
    Invoke-PublishArchive -Runtime $rid -TargetOS $targetOs `
        -SelfContained $true  -Suffix ""         -Label "self-contained"

    # Minimal: framework-dependent, requires the .NET runtime on the target.
    Invoke-PublishArchive -Runtime $rid -TargetOS $targetOs `
        -SelfContained $false -Suffix "_minimal" -Label "minimal / framework-dependent"
}

Write-Host "Done. Artifacts written to $distDir" -ForegroundColor Green
foreach ($a in $artifacts) {
    Write-Host "  $a"
}

if ($anyLinux) {
    Write-Host ""
    Write-Host "NOTE: the Linux archives were built on Windows, whose filesystem has no" -ForegroundColor Yellow
    Write-Host "      executable bit to record. After extracting, run:"                  -ForegroundColor Yellow
    Write-Host "        chmod +x $projectName"                                           -ForegroundColor Yellow
}
