[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\release"),
    [string]$ArtifactVersion = "local",
    [string]$ObsVersion = "32.2.1",
    [string]$ObsZipPath = ""
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

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($releaseRoot.TrimEnd("\") -eq $repoRoot.TrimEnd("\")) {
    throw "OutputDirectory cannot be the repository root."
}

if (Test-Path -LiteralPath $releaseRoot) {
    [System.IO.Directory]::Delete($releaseRoot, $true)
}
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
$packageRoot = Join-Path $releaseRoot "streamer"
$appOutput = Join-Path $packageRoot "_app"
$obsRoot = Join-Path $packageRoot "obs-portable"
$obsAppRoot = Join-Path $obsRoot "app"
New-Item -ItemType Directory -Path $appOutput, $obsAppRoot | Out-Null

$checksums = @{
    "32.2.0" = "793c8866796f94b907646c63ac73b9d0df7e98dcdab5b6bfd5baebf153e1ed92"
    "32.2.1" = "db64a2934f8261f85b1410b84be011207a0afda5400d008289f1f1e211bcc7de"
}
$obsAsset = "OBS-Studio-$ObsVersion-Windows-x64.zip"
$obsZip = Join-Path $releaseRoot $obsAsset

if ([string]::IsNullOrWhiteSpace($ObsZipPath)) {
    $obsUrl = "https://github.com/obsproject/obs-studio/releases/download/$ObsVersion/$obsAsset"
    Write-Host "Downloading $obsUrl"
    Invoke-WebRequest -Uri $obsUrl -OutFile $obsZip
} else {
    $resolvedObsZip = (Resolve-Path -LiteralPath $ObsZipPath).Path
    Copy-Item -LiteralPath $resolvedObsZip -Destination $obsZip
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $obsZip).Hash.ToLowerInvariant()
if ($checksums.ContainsKey($ObsVersion)) {
    if ($actualHash -ne $checksums[$ObsVersion]) {
        throw "OBS checksum mismatch. Expected $($checksums[$ObsVersion]), got $actualHash."
    }
} else {
    Write-Warning "OBS version $ObsVersion is not pinned in the checksum table; add its official SHA-256 before releasing."
}

Expand-Archive -LiteralPath $obsZip -DestinationPath $obsAppRoot -Force
if (-not (Test-Path -LiteralPath (Join-Path $obsAppRoot "bin\64bit\obs64.exe"))) {
    throw "The OBS archive did not contain app\bin\64bit\obs64.exe."
}

# PDBs and local OBS diagnostics are useful to developers but needlessly bloat
# a portable user release and can contain machine-specific details.
Get-ChildItem -LiteralPath $obsRoot -Recurse -File -Filter "*.pdb" | Remove-Item -Force

$obsConfigSource = Join-Path $repoRoot "packaging\obs-config"
$obsConfigDestination = Join-Path $obsAppRoot "config\obs-studio"
New-Item -ItemType Directory -Path $obsConfigDestination | Out-Null
Copy-Item -Path (Join-Path $obsConfigSource "*") -Destination $obsConfigDestination -Recurse -Force

$randomBytes = New-Object byte[] 24
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($randomBytes)
} finally {
    $random.Dispose()
}
$websocketPassword = [Convert]::ToBase64String($randomBytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
$websocketTemplate = Join-Path $obsConfigDestination "plugin_config\obs-websocket\config.template.json"
$websocketConfig = Get-Content -Raw -LiteralPath $websocketTemplate | ConvertFrom-Json
$websocketConfig.server_password = $websocketPassword
$websocketConfig | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath (Join-Path $obsConfigDestination "plugin_config\obs-websocket\config.json")
[System.IO.File]::Delete($websocketTemplate)

$panelArgs = @(
    "-m", "PyInstaller", "--noconfirm", "--clean", "--onefile", "--noconsole",
    "--name", "painel-transmissao", "--distpath", $appOutput,
    "--workpath", (Join-Path $repoRoot "src\_build\painel"),
    "--specpath", (Join-Path $repoRoot "src\_build"),
    "--add-data", ((Join-Path $repoRoot "src\escolher-transmissao.py") + ";."),
    "--hidden-import", "websocket",
    "--hidden-import", "tkinter",
    "--hidden-import", "_tkinter",
    "--hidden-import", "tkinter.messagebox",
    "--hidden-import", "tkinter.ttk",
    "--hidden-import", "ctypes.wintypes",
    (Join-Path $repoRoot "src\painel-transmissao.py")
)
Invoke-Checked -FilePath "python" -Arguments $panelArgs

$statusArgs = @(
    "-m", "PyInstaller", "--noconfirm", "--clean", "--onefile", "--noconsole",
    "--name", "status-transmissao", "--distpath", $appOutput,
    "--workpath", (Join-Path $repoRoot "src\_build\status"),
    "--specpath", (Join-Path $repoRoot "src\_build"),
    (Join-Path $repoRoot "src\status-transmissao.py")
)
Invoke-Checked -FilePath "python" -Arguments $statusArgs

$publishRoot = Join-Path $releaseRoot "_supervisor-publish"
$dotnetArgs = @(
    "publish", (Join-Path $repoRoot "VDO-Ninja-Streamer.csproj"),
    "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true",
    "--output", $publishRoot,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:InvariantGlobalization=true"
)
Invoke-Checked -FilePath "dotnet" -Arguments $dotnetArgs
Copy-Item -LiteralPath (Join-Path $publishRoot "VDO-Ninja-Streamer.exe") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "TRANSMITIR.cmd") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $packageRoot "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\README-release.txt") -Destination (Join-Path $packageRoot "LEIA-ME.txt")

[System.IO.Directory]::Delete($publishRoot, $true)
[System.IO.File]::Delete($obsZip)
$safeVersion = $ArtifactVersion -replace "[^A-Za-z0-9._-]", "-"
$artifactPath = Join-Path $releaseRoot "streamer-$safeVersion-windows-x64.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $packageRoot,
    $artifactPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

Write-Host "Package: $packageRoot"
Write-Host "Archive: $artifactPath"
