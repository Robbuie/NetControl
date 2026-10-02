<#
.SYNOPSIS
    Builds the single self-contained exe that goes on a plant laptop, and the manifest that tells
    an older copy it is out of date.

.DESCRIPTION
    PublishSingleFile and SelfContained are deliberately NOT in NetControl.App.csproj: setting them
    there pins a RuntimeIdentifier onto every ordinary build and every test run. They belong to the
    act of shipping, so they live here.

    The short commit hash is passed as SourceRevisionId, which the SDK appends to
    AssemblyInformationalVersion. The result reports itself as 0.5.0+a1b2c3d rather than 0.5.0, and
    that suffix is the difference between a version and a build - which is what a commissioning
    record from six months ago needs in order to be interpreted.

    A working tree with uncommitted changes is stamped -dirty. It still builds; it is just marked,
    because an exe that cannot be traced back to a commit is one nobody should be handed.

.PARAMETER Configuration
    Release unless you are debugging the publish itself.

.PARAMETER Runtime
    win-x64. There has never been a plant laptop here that was anything else.

.PARAMETER DownloadUrl
    Written into version.json as where this build can be fetched from. Optional: it defaults to the
    repository's releases page, which is where the app itself looks.

.PARAMETER Installer
    Also wrap the exe with Inno Setup, producing dist_installer\NetControl-Setup-<version>.exe.
    Needs Inno Setup 6 on the machine. The release workflow passes this; a local build does not
    have to.

.PARAMETER SkipTests
    Do not run the suite first. For iterating on the packaging itself, and for the release workflow,
    which has already run the tests as their own step so a failure says "tests" rather than
    "publish". Never for a build anybody is going to be handed.

.EXAMPLE
    pwsh tools/publish.ps1
    pwsh tools/publish.ps1 -Installer
    pwsh tools/publish.ps1 -DownloadUrl https://intranet.example/tools/netcontrol/
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $DownloadUrl,
    [string] $Notes,
    [switch] $Installer,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/NetControl.App/NetControl.App.csproj'

# --- version -----------------------------------------------------------------------------------
# One place, Directory.Build.props, read rather than duplicated. A version in two files is a
# version that disagrees with itself the first time somebody is in a hurry.
[xml] $props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1

if (-not $version) {
    throw 'Directory.Build.props does not set VersionPrefix, so there is no version to publish.'
}

$revision = ''
try {
    $revision = (& git -C $repo rev-parse --short HEAD 2>$null).Trim()
    if (& git -C $repo status --porcelain) {
        Write-Warning 'The working tree has uncommitted changes; this build will be stamped -dirty.'
        $revision = "$revision-dirty"
    }
}
catch {
    Write-Warning 'No git here, so the build cannot be traced to a commit.'
}

$full = if ($revision) { "$version+$revision" } else { $version }
$output = Join-Path $repo "artifacts/NetControl-$version"

Write-Host "Publishing NetControl $full to $output" -ForegroundColor Cyan

# --- build -------------------------------------------------------------------------------------
# Tests first, always. Publishing something that has not passed them is how a laptop ends up with a
# build nobody can account for. -SkipTests exists for the release workflow, which runs them as their
# own step so that a red build says which half failed.
if ($SkipTests) {
    Write-Warning 'Skipping the test suite. Do not hand anybody a build produced this way.'
}
else {
    & dotnet test (Join-Path $repo 'NetControl.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing published.' }
}

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained',
    '-p:PublishSingleFile=true',
    '-o', $output,
    '--nologo'
)

if ($revision) { $publishArgs += "-p:SourceRevisionId=$revision" }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# --- what goes in the envelope -----------------------------------------------------------------
$exe = Join-Path $output 'NetControl.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe and it is not there." }

# So a copy that arrived by email or on a USB stick can be checked against the one that was built.
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
"$hash  NetControl.exe" | Set-Content -Path "$exe.sha256" -Encoding ascii

# The update manifest.
#
# The app asks GitHub for the latest release by default and needs none of this. version.json is for
# the site that mirrors builds onto an intranet share because its laptops cannot reach github.com,
# which is the ordinary shape of things in a plant - it points settings.json at wherever this file
# ends up. See DEPLOY.md.
$manifest = [ordered] @{ version = $full }
$manifest.url = if ($DownloadUrl) { $DownloadUrl } else { 'https://github.com/Robbuie/NetControl/releases/latest' }
if ($Notes) { $manifest.notes = $Notes }

$manifest | ConvertTo-Json | Set-Content -Path (Join-Path $output 'version.json') -Encoding utf8

# --- the installer -------------------------------------------------------------------------------
# Optional, and the exe above is not a by-product of it: both are published on every release,
# because the laptop somebody was handed this morning wants the loose file and the one they use
# every week wants a Start menu entry and a path that stays put.
$setup = $null

if ($Installer) {
    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        throw 'Inno Setup 6 is not installed. Get it from https://jrsoftware.org/isdl.php, or run without -Installer.'
    }

    $script = Join-Path $repo 'installer/NetControl.iss'

    # An absolute SourceDir, deliberately: a relative Source in an .iss resolves against the script's
    # own folder, and a path that silently resolves somewhere else produces an installer containing
    # nothing rather than an error.
    & $iscc "/DAppVersion=$version" "/DSourceDir=$output" $script
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

    $setup = Join-Path $repo "dist_installer/NetControl-Setup-$version.exe"
    if (-not (Test-Path $setup)) { throw "Expected $setup and it is not there." }

    $setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash
    "$setupHash  NetControl-Setup-$version.exe" | Set-Content -Path "$setup.sha256" -Encoding ascii
}

Write-Host ''
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  SHA256 $hash"
Write-Host "  version.json -> $full"

if ($setup) {
    Write-Host ''
    Write-Host "  $setup" -ForegroundColor Green
    Write-Host "  SHA256 $setupHash"
}

Write-Host ''
Write-Host 'Two ways to get this onto a laptop, and both are published on every release:'
Write-Host '  the installer, for a machine somebody uses every week;'
Write-Host '  the loose exe, for one they were handed this morning. Copy it anywhere and run it.'
