<#
.SYNOPSIS
    Smoke-tests a published glacier-cli MCP server over standard input/output.

.DESCRIPTION
    Starts "<binary> mcp --headless", performs the MCP handshake (initialize + notifications/initialized),
    lists the tools, resources and prompts, reads glacier://guide, its section index and section 3, renders the
    fix_leaks prompt, and calls session_info, new_level, run_lua and level_summary. Every line the server
    writes to standard output must be a JSON-RPC 2.0 message; anything else fails the smoke. Diagnostics
    from standard error are printed at the end.

    With -Wsl the binary is a Linux path run inside WSL (wsl.exe -e <binary> mcp ...), so the Windows host
    can smoke-test the linux-x64 single-file build.

    With -Attach the server runs "mcp --attach" against the running editor instead: session_info must report
    mode "attach"; then new_level (only when the editor has no level open; otherwise the open level is used),
    a run_lua that builds two lit rooms joined by a corridor with the design kit plus a sealed build (one undo
    step, "AI: smoke-rooms.lua"), render_plan (the PNG is saved to -OutDir when given), level_summary, the
    -BeforeUndo script block (e.g. to capture the editor window), and undo. Nothing is saved.

.EXAMPLE
    tools\mcp-smoke.ps1 -Binary dist\Glacier-win-x64\glacier-cli.exe

.EXAMPLE
    tools\mcp-smoke.ps1 -Binary /mnt/j/glacier/dist/Glacier-linux-x64/glacier-cli -Wsl

.EXAMPLE
    tools\mcp-smoke.ps1 -Binary src\Ged.Cli\bin\Debug\net8.0\glacier-cli.exe -Attach -OutDir out\mcp-attach
#>
param(
    [Parameter(Mandatory = $true)][string]$Binary,
    [switch]$Wsl,
    [switch]$Attach,
    [string]$OutDir,
    [scriptblock]$BeforeUndo,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

$mode = if ($Attach) { '--attach' } else { '--headless' }
$psi = New-Object System.Diagnostics.ProcessStartInfo
if ($Wsl) {
    $psi.FileName = 'wsl.exe'
    $psi.Arguments = "-e `"$Binary`" mcp $mode"
} else {
    $psi.FileName = $Binary
    $psi.Arguments = "mcp $mode"
}
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)

$proc = [System.Diagnostics.Process]::Start($psi)
$stderrTask = $proc.StandardError.ReadToEndAsync()
$bad = New-Object System.Collections.Generic.List[string]

function Send($obj) {
    $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 20 -Compress))
    $proc.StandardInput.Flush()
}

function Receive([int]$id) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $proc.StandardOutput.ReadLine()
        if ($null -eq $line) { throw "The server closed standard output before answering request $id." }
        try { $msg = $line | ConvertFrom-Json } catch { $bad.Add($line); continue }
        if ($msg.jsonrpc -ne '2.0') { $bad.Add($line); continue }
        if ($msg.id -eq $id) { return $msg }
    }
    throw "No answer to request $id within $TimeoutSeconds s."
}

function CallTool([int]$id, [string]$name, $arguments) {
    Send @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $name; arguments = $arguments } }
    $r = Receive $id
    $text = $r.result.content[0].text
    $state = if ($r.result.isError) { 'ERROR' } else { 'ok' }
    Write-Host ("tools/call {0}: {1}  {2}" -f $name, $state, $text.Substring(0, [Math]::Min(160, $text.Length)))
    return $r
}

try {
    Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'mcp-smoke'; version = '1' } } }
    $init = Receive 1
    Write-Host ("initialize: protocol {0}, server {1} {2}, instructions {3} chars" -f $init.result.protocolVersion, $init.result.serverInfo.name, $init.result.serverInfo.version, $init.result.instructions.Length)
    Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $tools = (Receive 2).result.tools
    Write-Host ("tools/list: {0} tools ({1})" -f $tools.Count, (($tools | ForEach-Object { $_.name }) -join ', '))

    $failed = $false
    Send @{ jsonrpc = '2.0'; id = 20; method = 'resources/list' }
    $resources = (Receive 20).result.resources
    Write-Host ("resources/list: {0} resources ({1})" -f $resources.Count, (($resources | ForEach-Object { $_.uri }) -join ', '))
    Send @{ jsonrpc = '2.0'; id = 21; method = 'resources/templates/list' }
    $templates = (Receive 21).result.resourceTemplates
    Write-Host ("resources/templates/list: {0}" -f (($templates | ForEach-Object { $_.uriTemplate }) -join ', '))
    Send @{ jsonrpc = '2.0'; id = 22; method = 'resources/read'; params = @{ uri = 'glacier://guide' } }
    $guide = (Receive 22).result.contents
    if ($null -eq $guide -or $guide[0].text.Length -lt 1000) { Write-Host 'resources/read glacier://guide returned no guide.' -ForegroundColor Red; $failed = $true }
    else { Write-Host ("resources/read glacier://guide: {0} chars" -f $guide[0].text.Length) }
    Send @{ jsonrpc = '2.0'; id = 25; method = 'resources/read'; params = @{ uri = 'glacier://guide/index' } }
    $index = (Receive 25).result.contents
    if ($null -eq $index -or $index[0].text -notmatch 'glacier://guide/3') { Write-Host 'resources/read glacier://guide/index lists no sections.' -ForegroundColor Red; $failed = $true }
    else { Write-Host ("resources/read glacier://guide/index: {0} chars" -f $index[0].text.Length) }
    Send @{ jsonrpc = '2.0'; id = 26; method = 'resources/read'; params = @{ uri = 'glacier://guide/3' } }
    $section = (Receive 26).result.contents
    if ($null -eq $section -or $section[0].text -notmatch '^## 3\. ' -or $section[0].text.Length -gt 20000) { Write-Host 'resources/read glacier://guide/3 did not return section 3.' -ForegroundColor Red; $failed = $true }
    else { Write-Host ("resources/read glacier://guide/3: {0} chars" -f $section[0].text.Length) }
    Send @{ jsonrpc = '2.0'; id = 23; method = 'prompts/list' }
    $prompts = (Receive 23).result.prompts
    Write-Host ("prompts/list: {0} prompts ({1})" -f $prompts.Count, (($prompts | ForEach-Object { $_.name }) -join ', '))
    Send @{ jsonrpc = '2.0'; id = 24; method = 'prompts/get'; params = @{ name = 'fix_leaks' } }
    $prompt = (Receive 24).result
    if ($null -eq $prompt -or $prompt.messages.Count -ne 1) { Write-Host 'prompts/get fix_leaks returned no message.' -ForegroundColor Red; $failed = $true }
    if ($resources.Count -lt 6 -or $prompts.Count -lt 4) { Write-Host 'resources or prompts are missing.' -ForegroundColor Red; $failed = $true }
    $info = CallTool 3 'session_info' @{}
    $failed = $failed -or [bool]$info.result.isError
    if ($Attach) {
        $infoJson = $info.result.content[0].text | ConvertFrom-Json
        if ($infoJson.mode -ne 'attach') { Write-Host "session_info reports mode '$($infoJson.mode)', expected 'attach'." -ForegroundColor Red; $failed = $true }
        if ($null -eq $infoJson.document) {
            $failed = $failed -or [bool](CallTool 4 'new_level' @{}).result.isError
        } else {
            Write-Host ("using the level open in the editor: {0}" -f $infoJson.document.name)
        }
        $rooms = @(
            'local kit = ged.import("kit")',
            'local west = kit.room{ name = "west", pos = {-9, 0, 0}, size = {10, 5, 10} }',
            'local east = kit.room{ name = "east", pos = {9, 0, 0}, size = {10, 5, 10} }',
            'kit.corridor{ name = "link", a = west, b = east, width = 3 }',
            'kit.light_room{ name = "west_light", room = west }',
            'kit.light_room{ name = "east_light", room = east }'
        ) -join "`n"
        $run = CallTool 5 'run_lua' @{ code = $rooms; name = 'smoke-rooms.lua'; build = 'sealed' }
        $failed = $failed -or [bool]$run.result.isError
        $render = CallTool 6 'render_plan' @{ size = '768x768' }
        $failed = $failed -or [bool]$render.result.isError
        if (-not $render.result.isError -and $OutDir) {
            New-Item -ItemType Directory -Force $OutDir | Out-Null
            $png = Join-Path $OutDir 'render-plan.png'
            [System.IO.File]::WriteAllBytes($png, [Convert]::FromBase64String($render.result.content[1].data))
            Write-Host "render_plan image: $png"
        }
        $failed = $failed -or [bool](CallTool 7 'level_summary' @{ detail = 'brief' }).result.isError
        if ($BeforeUndo) { & $BeforeUndo }
        $undo = CallTool 8 'undo' @{}
        $failed = $failed -or [bool]$undo.result.isError
    } else {
        $failed = $failed -or [bool](CallTool 4 'new_level' @{}).result.isError
        $failed = $failed -or [bool](CallTool 5 'run_lua' @{ code = "local b = level.place_box(0, 2, 0, 8, 4, 8)`nb:set_air(true)`nreturn level.brush_count"; label = 'smoke' }).result.isError
        $failed = $failed -or [bool](CallTool 6 'level_summary' @{ detail = 'brief' }).result.isError
    }

    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); throw 'The server did not exit after its input closed.' }
    Write-Host ("exit code: {0}" -f $proc.ExitCode)
    Write-Host 'stderr:'
    Write-Host $stderrTask.Result
    if ($bad.Count -gt 0) {
        Write-Host ("FAIL: {0} non-JSON-RPC line(s) on standard output:" -f $bad.Count) -ForegroundColor Red
        $bad | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    if ($failed -or $proc.ExitCode -ne 0) { Write-Host 'FAIL: a tool call failed or the server exited with an error.' -ForegroundColor Red; exit 1 }
    Write-Host ("PASS: handshake, tools/list and tool calls over stdio ({0}); standard output was pure JSON-RPC." -f $mode) -ForegroundColor Green
    exit 0
} finally {
    if (-not $proc.HasExited) { $proc.Kill() }
}
