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
    Written into version.json as where this build can be fetched from. Optional: without it the
    manifest still carries the version, which is the part the update check needs.

.EXAMPLE
    pwsh tools/publish.ps1
    pwsh tools/publish.ps1 -DownloadUrl https://intranet.example/tools/netcontrol/
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $DownloadUrl,
    [string] $Notes
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
# build nobody can account for.
& dotnet test (Join-Path $repo 'NetControl.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing published.' }

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

# The update manifest. Publish this file where settings.json points; see DEPLOY.md.
$manifest = [ordered] @{ version = $full }
if ($DownloadUrl) { $manifest.url = $DownloadUrl }
if ($Notes) { $manifest.notes = $Notes }

$manifest | ConvertTo-Json | Set-Content -Path (Join-Path $output 'version.json') -Encoding utf8

Write-Host ''
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  SHA256 $hash"
Write-Host "  version.json -> $full"
Write-Host ''
Write-Host 'Copy the folder. There is no installer and there is deliberately not going to be one.'
