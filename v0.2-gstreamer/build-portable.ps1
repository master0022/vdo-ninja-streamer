[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "release"),
    [string]$ArtifactVersion = "v0.2-av1",
    [string]$GStreamerVersion = "1.28.6",
    [string]$GStreamerInstallerPath = ""
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path ([IO.Path]::GetTempPath()) ("StreamerV2-GStreamer-" + $GStreamerVersion)
$installer = Join-Path $stage ("gstreamer-1.0-msvc-x86_64-" + $GStreamerVersion + ".exe")
$runtime = Join-Path $stage "runtime"
$url = "https://gstreamer.freedesktop.org/data/pkg/windows/$GStreamerVersion/msvc/gstreamer-1.0-msvc-x86_64-$GStreamerVersion.exe"

New-Item -ItemType Directory -Force -Path $stage | Out-Null

if (-not [string]::IsNullOrWhiteSpace($GStreamerInstallerPath)) {
    $providedInstaller = [IO.Path]::GetFullPath($GStreamerInstallerPath)
    if (-not $providedInstaller.Equals([IO.Path]::GetFullPath($installer), [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $providedInstaller -Destination $installer -Force
    }
} elseif (-not (Test-Path -LiteralPath $installer)) {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $installer
}

if (-not (Test-Path -LiteralPath (Join-Path $runtime "bin\gst-inspect-1.0.exe"))) {
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    $arguments = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/CURRENTUSER",
        "/TYPE=runtime",
        ("/DIR=" + $runtime)
    )
    $installerProcess = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    if ($installerProcess.ExitCode -ne 0) {
        throw "GStreamer installer failed with exit code $($installerProcess.ExitCode)."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $runtime "lib\gstreamer-1.0\gstnvcodec.dll"))) {
    throw "GStreamer runtime did not contain gstnvcodec.dll."
}

$publishRoot = Join-Path $releaseRoot "publish"
$packageRoot = Join-Path $releaseRoot ("StreamerV2-" + $ArtifactVersion + "-win-x64")
$zipPath = Join-Path $releaseRoot ("StreamerV2-" + $ArtifactVersion + "-win-x64.zip")
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path -LiteralPath $publishRoot) { [IO.Directory]::Delete($publishRoot, $true) }
if (Test-Path -LiteralPath $packageRoot) { [IO.Directory]::Delete($packageRoot, $true) }
if (Test-Path -LiteralPath $zipPath) { [IO.File]::Delete($zipPath) }

$dotnetArguments = @(
    "publish", (Join-Path $root "StreamerV2.csproj"),
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $publishRoot,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    ("-p:GStreamerRuntimeSource=" + $runtime)
)
Invoke-Checked -FilePath "dotnet" -Arguments $dotnetArguments

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot "StreamerV2.exe") -Destination $packageRoot
foreach ($directory in @("gstreamer", "runtimes", "WebView2")) {
    $sourceDirectory = Join-Path $publishRoot $directory
    if (Test-Path -LiteralPath $sourceDirectory) {
        Copy-Item -LiteralPath $sourceDirectory -Destination $packageRoot -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "docs") | Out-Null
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $packageRoot "docs\README.md")
Copy-Item -LiteralPath (Join-Path $root "TEST-PLAN.md") -Destination (Join-Path $packageRoot "docs\TEST-PLAN.md")

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageRoot,
    $zipPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)

Write-Host "Package: $packageRoot"
Write-Host "Archive: $zipPath"
