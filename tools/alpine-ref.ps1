# Generates Alpine RED reference builds ("Alpine tier", docs/internal/ALPINE-COMPAT-SPEC.md §7.3) with Alpine
# Faction's headless bake: AlpineFactionLauncher.exe -editor -exe-path <RED.exe> -bake <in> -bakeout <out>
# (Alpine docs/reference/COMMAND_LINE.md:77-97, editor_patch/headless_bake.cpp:151-253).
#
# Never run in CI. Needs a desktop session (RED creates its window and D3D device). Each input level is COPIED to a
# scratch folder first — a source level is never baked in place. The Alpine binaries are copied out of -AlpineBin
# to a scratch folder outside any git checkout and run from there. RED writes its own logs and history next to
# RED.exe, so -RedExe must be a sandbox install set aside for this. Only the RED process this script started is
# ever stopped. Build repair follows that install's alpine_editor.ini ([Alpine] RepairBuildHoles, default on); the
# state is recorded in the manifest, never changed.
#
# Output: <OutRoot>\<alpine id>\<build|bake|bake-raw>\<level>.rfl + <level>.rfl.log + manifest.json (research\ is gitignored; tests that
# read these skip when absent).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\alpine-ref.ps1 -RedExe <sandbox>\RED.exe
#       (-Levels a.rfl,b.rfl | -LevelDir research\example_rfls) [-BuildOnly] [-RawLightmaps] [-AlpineBin research\alpine_current_master]
#       [-AlpineSource <alpine checkout, read for its HEAD only>] [-OutRoot research\fixtures\alpine-ref] [-TimeoutMin 60]
param(
    [Parameter(Mandatory = $true)][string]$RedExe,
    [string[]]$Levels,
    [string]$LevelDir,
    [string]$AlpineBin,
    [string]$AlpineSource,
    [string]$BinDir,
    [string]$OutRoot,
    [switch]$BuildOnly,
    [switch]$RawLightmaps,
    [double]$TimeoutMin = 60
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no $PSScriptRoot in parameter defaults.
$repoRoot = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
if (-not $AlpineBin) { $AlpineBin = Join-Path $repoRoot 'research\alpine_current_master' }
if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot 'research\fixtures\alpine-ref' }
# -Levels takes a comma list; -LevelDir every .rfl in a folder (autosaves skipped). From `powershell -File`, pass
# several levels as one comma-separated string.
$Levels = @($Levels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($LevelDir) { $Levels += @(Get-ChildItem $LevelDir -Filter *.rfl -File | Where-Object { $_.Name -notlike '*.autosave.rfl' } | ForEach-Object FullName) }
if (-not $Levels.Count) { throw 'no levels: pass -Levels or -LevelDir' }

function Test-InGitCheckout([string]$path) {
    for ($d = $path; $d; $d = Split-Path $d -Parent) { if (Test-Path (Join-Path $d '.git')) { return $true } }
    return $false
}

$RedExe = (Resolve-Path $RedExe).Path
$redDir = Split-Path $RedExe -Parent
$AlpineBin = (Resolve-Path $AlpineBin).Path
$launcherSrc = Join-Path $AlpineBin 'AlpineFactionLauncher.exe'
if (-not (Test-Path $launcherSrc)) { throw "no AlpineFactionLauncher.exe in $AlpineBin" }

# Identity of the Alpine build: SHA-256 of AlpineEditor.dll (+ the source HEAD when given).
$editorDll = Join-Path $AlpineBin 'AlpineEditor.dll'
$binSha = (Get-FileHash -Algorithm SHA256 $(if (Test-Path $editorDll) { $editorDll } else { $launcherSrc })).Hash.ToLower()
$srcHead = $null
if ($AlpineSource) {
    $head = Join-Path $AlpineSource '.git\HEAD'
    if (Test-Path $head) {
        $h = (Get-Content $head -Raw).Trim()
        if ($h -like 'ref: *') { $ref = Join-Path $AlpineSource ('.git\' + $h.Substring(5)); if (Test-Path $ref) { $h = (Get-Content $ref -Raw).Trim() } }
        $srcHead = $h
    }
}
$alpineId = if ($srcHead) { $srcHead.Substring(0, 8) } else { 'bin-' + $binSha.Substring(0, 12) }

# Repair state as RED will see it (read only).
$repair = $true
$ini = Join-Path $redDir 'alpine_editor.ini'
if (Test-Path $ini) {
    $m = Select-String -Path $ini -Pattern '^\s*RepairBuildHoles\s*=\s*(\S+)' | Select-Object -First 1
    if ($m) { $repair = @('1', 'true', 'yes', 'on') -contains $m.Matches[0].Groups[1].Value.ToLower() }
}

# Scratch: binaries copy + per-level input copies (ASCII path under %TEMP%).
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("glacier-alpine-ref-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
# -BinDir: a persistent folder (outside any git checkout) the binaries are synced into and run from — e.g. one the
# machine's antivirus has been told about; without it a fresh scratch copy is used.
$bin = if ($BinDir) { [IO.Path]::GetFullPath($BinDir) } else { Join-Path $scratch 'bin' }
$work = Join-Path $scratch 'in'
New-Item -ItemType Directory -Force $bin, $work | Out-Null
$inPlace = $bin.TrimEnd('\').ToLower() -eq $AlpineBin.TrimEnd('\').ToLower()
if ($inPlace) {
    # Run the binaries where they are (allowed only when git ignores that folder, e.g. research\).
    Push-Location $bin
    try { git check-ignore -q . 2>$null; $ignored = $LASTEXITCODE -eq 0 } finally { Pop-Location }
    if ((Test-InGitCheckout $bin) -and -not $ignored) { throw "refusing: $bin is tracked by a git checkout" }
} else {
    if (Test-InGitCheckout $bin) { throw "refusing: $bin is inside a git checkout" }
    Copy-Item (Join-Path $AlpineBin '*') $bin -Recurse -Force
    Start-Sleep -Seconds 2
}
if (-not (Test-Path (Join-Path $bin 'AlpineFactionLauncher.exe'))) {
    throw "AlpineFactionLauncher.exe disappeared from $bin after copying (antivirus quarantine?). Allow it for that folder, or pass -BinDir <folder>."
}

$mode = if ($BuildOnly) { 'build' } elseif ($RawLightmaps) { 'bake-raw' } else { 'bake' }
$outDir = Join-Path (Join-Path ([IO.Path]::GetFullPath($OutRoot)) $alpineId) $mode
New-Item -ItemType Directory -Force $outDir | Out-Null
$exes = @($RedExe.ToLower(), (Join-Path $redDir 'RED_laa.exe').ToLower())
$foreign = @(Get-CimInstance Win32_Process -Filter "Name='RED.exe' OR Name='RED_laa.exe'" | Where-Object { $_.ExecutablePath -and $exes -contains $_.ExecutablePath.ToLower() })
if ($foreign.Count) { throw "a RED from $redDir is already running (pid $($foreign.ProcessId -join ',')); not ours to touch" }

$results = @()
foreach ($lvl in $Levels) {
    $src = (Resolve-Path $lvl).Path
    $name = [IO.Path]::GetFileName($src)
    $in = Join-Path $work $name
    Copy-Item $src $in -Force
    $out = Join-Path $scratch ("out-" + $name)
    Remove-Item $out, "$out.log" -Force -ErrorAction SilentlyContinue

    $snapshot = @{}
    Get-CimInstance Win32_Process -Filter "Name='RED.exe' OR Name='RED_laa.exe'" | ForEach-Object { $snapshot[[int]$_.ProcessId] = $true }
    $argList = @('-editor', '-exe-path', $RedExe, '-bake', $in, '-bakeout', $out)
    if ($BuildOnly) { $argList += '-buildonly' }
    if ($RawLightmaps) { $argList += '-rawlightmaps' }
    $t0 = Get-Date
    $launcher = Start-Process -FilePath (Join-Path $bin 'AlpineFactionLauncher.exe') -ArgumentList $argList -WorkingDirectory $bin -PassThru
    $redPid = $null
    $until = (Get-Date).AddSeconds(90)
    while (-not $redPid -and (Get-Date) -lt $until) {
        # A -buildonly save can finish before a process poll ever sees RED running.
        if ($launcher.HasExited -and (Test-Path "$out.log") -and (Select-String -Path "$out.log" -Pattern 'done rc=' -Quiet)) { break }
        $c = @(Get-CimInstance Win32_Process -Filter "Name='RED.exe' OR Name='RED_laa.exe'" | Where-Object {
                $_.ExecutablePath -and $exes -contains $_.ExecutablePath.ToLower() -and -not $snapshot.ContainsKey([int]$_.ProcessId) })
        if ($c.Count) { $redPid = [int]$c[0].ProcessId } else { Start-Sleep -Milliseconds 500 }
    }
    $deadline = $t0.AddMinutes($TimeoutMin)
    $timedOut = $false
    while ($redPid -and (Get-Process -Id $redPid -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) { Stop-Process -Id $redPid -Force -ErrorAction SilentlyContinue; $timedOut = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $launcher.HasExited) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='CrashHandler.exe'") {
        if (($redPid -and [int]$p.ParentProcessId -eq $redPid) -or [int]$p.ParentProcessId -eq $launcher.Id) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    }

    $rc = $null; $counters = [ordered]@{}
    if (Test-Path "$out.log") {
        foreach ($line in Get-Content "$out.log") {
            if ($line -match 'done rc=(\d+)') { $rc = [int]$Matches[1] }
            # BuildRepair counters (editor_patch/build_repair.cpp:1507-1522): "<label>: <n>"
            if ($line -match '^\[\s*[\d.]+\]\s+([A-Z][A-Za-z ]+):\s+(\d+)\s*$') { $counters[$Matches[1]] = [int]$Matches[2] }
        }
        Copy-Item "$out.log" (Join-Path $outDir "$name.log") -Force
    }
    $ok = (Test-Path $out) -and $rc -eq 0
    if (Test-Path $out) { Copy-Item $out (Join-Path $outDir $name) -Force }
    $results += [ordered]@{
        level = $name; sourceSha256 = (Get-FileHash -Algorithm SHA256 $src).Hash.ToLower()
        rc = $rc; ok = $ok; timedOut = $timedOut; seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
        repairCounters = $counters
    }
    "{0,-28} rc={1} {2} ({3:0.0} s)" -f $name, $rc, $(if ($ok) { 'ok' } else { 'FAILED' }), ((Get-Date) - $t0).TotalSeconds
}

$manifestPath = Join-Path $outDir 'manifest.json'
$prior = @()
if (Test-Path $manifestPath) { $prior = @((Get-Content $manifestPath -Raw | ConvertFrom-Json).levels | Where-Object { $n = $_.level; -not ($results | Where-Object { $_.level -eq $n }) }) }
[ordered]@{
    alpineId = $alpineId; alpineBinarySha256 = $binSha; alpineSourceHead = $srcHead
    repairBuildHoles = $repair; buildOnly = [bool]$BuildOnly; rawLightmaps = [bool]$RawLightmaps
    generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    levels = @($prior) + @($results)
} | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $manifestPath
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
"manifest: $manifestPath"
