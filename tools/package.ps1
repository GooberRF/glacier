<#
.SYNOPSIS
    Builds a self-contained, SINGLE-FILE Release publish of Glacier and packages it with
    LICENSE, licensing-info.txt, the offline help reference (docs\help.html) and the bundled
    scripts library for distribution. Supports win-x64 (zip) and linux-x64 (tar.gz + AppImage).

.DESCRIPTION
    Publishes src/Ged.App as a self-contained single-file app (native libraries embedded
    for self-extract, so the distribution is the Glacier binary + docs), stages the
    user-facing docs and the bundled scripts library alongside the binary, and produces
    Glacier-<version>-<rid>.(zip|tar.gz) in the output directory. For linux-x64 it ALSO
    builds Glacier-<version>-linux-x86_64.AppImage (single-file, no-install) by invoking
    appimagetool through WSL2 on a Windows host (or directly on a Linux host). Does NOT
    touch the coordinator's build tree.

.PARAMETER OutputDir
    Where the publish folders + archives are written. Default: <repo>\dist

.PARAMETER Runtime
    Target runtime identifier: win-x64 (default), linux-x64, or all.

.PARAMETER NoAppImage
    Skip the AppImage step for linux-x64 (still produces the tar.gz). Useful on hosts
    without WSL2 / a Linux build environment.

.EXAMPLE
    pwsh tools/package.ps1                      # win-x64 zip
    pwsh tools/package.ps1 -Runtime linux-x64   # linux-x64 tar.gz + AppImage
    pwsh tools/package.ps1 -Runtime all         # win zip + linux tar.gz + AppImage
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string]$Runtime = 'win-x64',
    [switch]$NoAppImage
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }

$app = Join-Path $repoRoot 'src\Ged.App\Ged.App.csproj'

# Read the shipping version from the csproj (RID-independent: a linux ELF has no
# Win32 VersionInfo to read back).
$csproj = Get-Content $app -Raw
$version = if ($csproj -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '1.0.0' }
$version = ($version -split '\+')[0]

# --- AppImage helpers (linux-x64) ------------------------------------------------------

# Convert a Windows drive path to its WSL /mnt/<drive>/... equivalent. Done in-process
# (not via `wsl wslpath`, which eats backslashes when they cross the wsl.exe arg boundary).
function ConvertTo-WslPath([string]$winPath) {
    $full = [System.IO.Path]::GetFullPath($winPath)
    if ($full -notmatch '^[A-Za-z]:\\') { throw "not a drive-letter path: $winPath" }
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    $rest  = $full.Substring(2) -replace '\\', '/'
    return "/mnt/$drive$rest"
}

# Derive a square 256x256 PNG icon from the source AppIcon.png (transparent letterbox,
# high-quality downscale). System.Drawing keeps this dependency-free on Windows.
function New-GlacierIcon([string]$srcPng, [string]$outPng, [int]$size = 256) {
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($srcPng)
    try {
        $side = [Math]::Max($src.Width, $src.Height)
        $square = New-Object System.Drawing.Bitmap $side, $side
        $sg = [System.Drawing.Graphics]::FromImage($square)
        $sg.Clear([System.Drawing.Color]::Transparent)
        $sg.DrawImage($src, [int](($side - $src.Width) / 2), [int](($side - $src.Height) / 2), $src.Width, $src.Height)
        $sg.Dispose()
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($square, 0, 0, $size, $size)
        $g.Dispose()
        $bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose(); $square.Dispose()
    }
    finally { $src.Dispose() }
}

# Build dist/Glacier-<version>-linux-x86_64.AppImage from a published linux-x64 folder by
# invoking tools/appimage/build-appimage.sh under WSL. appimagetool is a build tool (fetched
# into the WSL user's HOME, never the repo); the .desktop + AppRun are first-party assets.
function New-GlacierAppImage([string]$publishDir, [string]$version) {
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
        Write-Warning "WSL not available - skipping AppImage (tar.gz still produced). Build it on a Linux host or under WSL2."
        return
    }
    $iconSrc = Join-Path $repoRoot 'src\Ged.App\Assets\AppIcon.png'
    $desktop = Join-Path $PSScriptRoot 'appimage\Glacier.desktop'
    $apprun  = Join-Path $PSScriptRoot 'appimage\AppRun'
    $builder = Join-Path $PSScriptRoot 'appimage\build-appimage.sh'
    foreach ($f in @($iconSrc, $desktop, $apprun, $builder)) {
        if (-not (Test-Path $f)) { Write-Warning "AppImage input missing: $f - skipping AppImage."; return }
    }

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("glacier-appimage-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        $iconPng = Join-Path $tmp 'Glacier.png'
        New-GlacierIcon $iconSrc $iconPng 256

        $outAppImage = Join-Path $OutputDir "Glacier-$version-linux-x86_64.AppImage"
        Write-Host "Building AppImage via WSL -> $outAppImage" -ForegroundColor Cyan

        # Under Windows PowerShell 5.1, a native command's stderr output is surfaced as
        # ErrorRecords; with $ErrorActionPreference='Stop' that aborts the script even when
        # the build succeeds (exit 0) and only benign WSL/appimagetool progress noise went
        # to stderr. Scope error handling to Continue across the native call and gate
        # success strictly on $LASTEXITCODE (not on the presence of stderr).
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & wsl.exe -e bash (ConvertTo-WslPath $builder) `
                (ConvertTo-WslPath $publishDir) `
                (ConvertTo-WslPath $iconPng) `
                (ConvertTo-WslPath $desktop) `
                (ConvertTo-WslPath $apprun) `
                (ConvertTo-WslPath $outAppImage)
        }
        finally { $ErrorActionPreference = $prevEap }
        if ($LASTEXITCODE -ne 0) { throw "AppImage build failed with exit code $LASTEXITCODE" }
        if (-not (Test-Path $outAppImage)) { throw "AppImage not produced: $outAppImage" }

        $sizeMb = [math]::Round((Get-Item $outAppImage).Length / 1MB, 1)
        Write-Host "  AppImage:       $outAppImage ($sizeMb MB)"
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

function Publish-Target([string]$rid) {
    $publishDir = Join-Path $OutputDir "Glacier-$rid"
    $binaryName = if ($rid -like 'win*') { 'Glacier.exe' } else { 'Glacier' }

    Write-Host ""
    Write-Host "Publishing $app (Release, self-contained, single-file, $rid)..." -ForegroundColor Cyan

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    # Single-file. Native libraries (Skia/HarfBuzz/ANGLE on win; libSkiaSharp.so/
    # libHarfBuzzSharp.so/libassimp.so.5 on linux) are embedded and self-extracted;
    # compression keeps the binary size reasonable. The license texts + bundled scripts
    # stay EXTERNAL next to the binary (Content items) so the About dialog can read them
    # and the scripts library is discoverable on first run.
    & dotnet publish $app `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($rid) with exit code $LASTEXITCODE" }

    # Debug symbols are not part of the user distribution.
    Get-ChildItem $publishDir -Filter '*.pdb' -Recurse | Remove-Item -Force

    # Stage the user-facing docs next to the binary (LICENSE + licensing-info.txt are
    # already copied by the csproj; the offline HTML help reference is added here for the
    # distribution). help.html lands at the publish root so it sits beside the exe,
    # which is where the in-app Help ▸ Help Topics (F1) resolver looks for it first.
    foreach ($doc in @('LICENSE', 'licensing-info.txt', 'docs\help.html')) {
        $src = Join-Path $repoRoot $doc
        if (Test-Path $src) { Copy-Item $src -Destination $publishDir -Force }
        else { Write-Warning "missing doc: $doc" }
    }

    $binary = Join-Path $publishDir $binaryName
    if (-not (Test-Path $binary)) { throw "published binary not found: $binary" }

    if ($rid -like 'win*') {
        $archive = Join-Path $OutputDir "Glacier-$version-$rid.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Write-Host "Zipping -> $archive" -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        # tar.gz for Linux (tar.exe ships with Win10 17063+). Run from OutputDir so paths
        # in the archive are relative. The Windows tar does not preserve a Unix +x bit, so
        # help.html's troubleshooting section tells users to `chmod +x Glacier` after
        # extracting (packaging on a Linux host, or an AppImage build, sets it directly).
        $archive = Join-Path $OutputDir "Glacier-$version-$rid.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }
        Write-Host "Tarring -> $archive" -ForegroundColor Cyan
        Push-Location $OutputDir
        try {
            & tar -czf $archive "Glacier-$rid"
            if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }

    $sizeMb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    $fileCount = (Get-ChildItem $publishDir -File).Count
    $binMb = [math]::Round((Get-Item $binary).Length / 1MB, 1)
    Write-Host "  Publish folder: $publishDir ($fileCount top-level files; $binaryName = $binMb MB)"
    Write-Host "  Archive:        $archive ($sizeMb MB)"

    # Linux also ships an AppImage (single-file, no-install) alongside the tar.gz.
    if ($rid -eq 'linux-x64' -and -not $NoAppImage) {
        New-GlacierAppImage $publishDir $version
    }
}

$targets = if ($Runtime -eq 'all') { @('win-x64', 'linux-x64') } else { @($Runtime) }

Write-Host "Repo:      $repoRoot"
Write-Host "Output:    $OutputDir"
Write-Host "Version:   $version"
Write-Host "Targets:   $($targets -join ', ')"

foreach ($rid in $targets) { Publish-Target $rid }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
