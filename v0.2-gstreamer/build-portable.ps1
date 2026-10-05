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

# The full runtime ships ~270 plugins (330 MB). GStreamer loads every plugin
# DLL on its first registry scan, so shipping only what the pipelines use cuts
# both the ZIP size and the first-launch time. Keep this list in sync with the
# factories created in GStreamerEngine.cs.
$requiredPlugins = @(
    "coreelements",       # queue, capsfilter, filesink
    "d3d11",              # d3d11screencapturesrc, d3d11convert, d3d11download
    "nvcodec",            # nvd3d11h264enc / h265enc / av1enc
    "amfcodec",           # amfh264enc (AMD)
    "qsv",                # qsvh264enc (Intel)
    "mediafoundation",    # mfh264enc (any GPU with a Windows hardware MFT)
    "x264",
    "videoconvertscale",  # videoconvert, videoscale
    "videoparsersbad",    # h264parse, h265parse, av1parse
    "rtp", "rsrtp",       # rtph264pay, rtph265pay, rtpopuspay, rtpav1pay
    "webrtchttp",         # whipsink
    "webrtc", "nice", "dtls", "srtp", "sctp", "rtpmanager",
    "wasapi2",
    "audioconvert", "audioresample", "volume", "opus",
    "matroska"            # local capture test
)

function Get-PeImports {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $optional = $pe + 24
    $dataDirectories = $optional + $(if ([BitConverter]::ToUInt16($bytes, $optional) -eq 0x20B) { 112 } else { 96 })
    $sections = $optional + $optionalSize

    $toOffset = {
        param([uint32]$rva)
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $s = $sections + 40 * $i
            $va = [BitConverter]::ToUInt32($bytes, $s + 12)
            $size = [Math]::Max([BitConverter]::ToUInt32($bytes, $s + 8), [BitConverter]::ToUInt32($bytes, $s + 16))
            if ($rva -ge $va -and $rva -lt $va + $size) {
                return [int]($rva - $va + [BitConverter]::ToUInt32($bytes, $s + 20))
            }
        }
        return -1
    }
    $readName = {
        param([int]$offset)
        $end = [Array]::IndexOf($bytes, [byte]0, $offset)
        [Text.Encoding]::ASCII.GetString($bytes, $offset, $end - $offset)
    }

    # Directory 1 = imports (20-byte descriptors, name at +12);
    # directory 13 = delay imports (32-byte descriptors, name at +4).
    foreach ($entry in @(@{ Index = 1; Size = 20; Name = 12 }, @{ Index = 13; Size = 32; Name = 4 })) {
        $rva = [BitConverter]::ToUInt32($bytes, $dataDirectories + 8 * $entry.Index)
        if ($rva -eq 0) { continue }
        $descriptor = & $toOffset $rva
        while ($descriptor -ge 0) {
            $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + $entry.Name)
            if ($nameRva -eq 0) { break }
            $nameOffset = & $toOffset $nameRva
            if ($nameOffset -ge 0) { & $readName $nameOffset }
            $descriptor += $entry.Size
        }
    }
}

$minimalRuntime = Join-Path $stage "runtime-minimal"
if (Test-Path -LiteralPath $minimalRuntime) { [IO.Directory]::Delete($minimalRuntime, $true) }
$pluginTarget = Join-Path $minimalRuntime "lib\gstreamer-1.0"
$gioTarget = Join-Path $minimalRuntime "lib\gio\modules"
New-Item -ItemType Directory -Force -Path (Join-Path $minimalRuntime "bin"), $pluginTarget, $gioTarget | Out-Null

$roots = @()
foreach ($plugin in $requiredPlugins) {
    $source = Join-Path $runtime "lib\gstreamer-1.0\gst$plugin.dll"
    if (-not (Test-Path -LiteralPath $source)) { throw "Required GStreamer plugin is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $pluginTarget
    $roots += $source
}
# GIO's TLS backend is loaded dynamically for HTTPS WHIP signalling.
foreach ($module in Get-ChildItem -LiteralPath (Join-Path $runtime "lib\gio\modules") -Filter *.dll) {
    Copy-Item -LiteralPath $module.FullName -Destination $gioTarget
    $roots += $module.FullName
}

$runtimeBin = Join-Path $runtime "bin"
$included = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$pending = New-Object System.Collections.Generic.Queue[string]
$roots | ForEach-Object { $pending.Enqueue($_) }
while ($pending.Count -gt 0) {
    foreach ($import in Get-PeImports -Path $pending.Dequeue()) {
        $candidate = Join-Path $runtimeBin $import
        if ((Test-Path -LiteralPath $candidate) -and $included.Add($import)) {
            Copy-Item -LiteralPath $candidate -Destination (Join-Path $minimalRuntime "bin")
            $pending.Enqueue($candidate)
        }
    }
}
foreach ($directory in @("etc\ssl")) {
    $source = Join-Path $runtime $directory
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $minimalRuntime $directory
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
    }
}
Write-Host ("Minimal GStreamer runtime: {0} plugins, {1} support DLLs" -f $requiredPlugins.Count, $included.Count)

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
    # Compression made every launch inflate ~100 MB in memory before Main ran.
    "-p:EnableCompressionInSingleFile=false",
    "-p:PublishReadyToRun=true",
    ("-p:GStreamerRuntimeSource=" + $minimalRuntime)
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
