# Capri Signal - end-to-end test against the Capri that is running right now.
# Shows real cards on the desktop. Writes the results to signal\test-results.txt.
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[string]
$failed = 0
function Check([string]$name, [bool]$ok, [string]$info = '') {
    $script:results.Add(($(if ($ok) { 'PASS ' } else { 'FAIL ' }) + $name + $(if (-not $ok -and $info) { "  -> $info" } else { '' })))
    if (-not $ok) { $script:failed++ }
}

$settingsPath = Join-Path $env:APPDATA 'Capri\signal.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$port = [int]$settings.activePort
Check 'Capri is listening (activePort set)' ($port -gt 0) "activePort=$port"
$base = "http://127.0.0.1:$port"

function Post([string]$path, [string]$json, [string]$token) {
    $headers = @{}
    if ($token) { $headers['X-Capri-Token'] = $token }
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri ($base + $path) -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 5
        return @{ Status = [int]$r.StatusCode; Body = $r.Content }
    } catch {
        if ($_.Exception.Response) { return @{ Status = [int]$_.Exception.Response.StatusCode; Body = '' } }
        return @{ Status = -1; Body = $_.Exception.Message }
    }
}
$t = [string]$settings.token
$run = [guid]::NewGuid().ToString('N').Substring(0, 6)

$h = Invoke-WebRequest -UseBasicParsing -Uri "$base/v1/health" -TimeoutSec 5
Check 'health' ($h.StatusCode -eq 200 -and $h.Content -like '*capri-signal*') $h.Content

$r = Post '/v1/event' "{`"type`":`"task.completed`",`"source`":`"codex`",`"task_id`":`"e2e-$run-1`",`"project`":`"Capri`",`"message`":`"E2E: Codex ha terminado.`"}" $t
Check 'completed -> card' ($r.Status -eq 200 -and $r.Body -like '*shown*') "$($r.Status) $($r.Body)"
$r = Post '/v1/event' "{`"type`":`"task.completed`",`"source`":`"codex`",`"task_id`":`"e2e-$run-1`",`"project`":`"Capri`",`"message`":`"E2E: Codex ha terminado.`"}" $t
Check 'duplicate dropped' ($r.Body -like '*duplicate*') $r.Body
$r = Post '/v1/event' "{`"type`":`"task.failed`",`"source`":`"claude-code`",`"task_id`":`"e2e-$run-2`",`"message`":`"E2E: fallo de compilacion.`"}" $t
Check 'failed -> card' ($r.Status -eq 200) "$($r.Status) $($r.Body)"
$r = Post '/v1/event' "{`"type`":`"task.needs_input`",`"source`":`"cursor`",`"task_id`":`"e2e-$run-3`"}" $t
Check 'needs_input -> card' ($r.Status -eq 200) "$($r.Status) $($r.Body)"
$r = Post '/v1/event' '{"type":"task.completed","source":"codex"}' ''
Check 'no token -> 401' ($r.Status -eq 401) $r.Status
$r = Post '/v1/event' '{"type":"task.completed","source":"codex"}' 'nope'
Check 'wrong token -> 401' ($r.Status -eq 401) $r.Status
$r = Post '/v1/event' '{"type": "task.completed",' $t
Check 'malformed -> 400' ($r.Status -eq 400) $r.Status
$ok = 0
1..4 | ForEach-Object { $x = Post '/v1/event' "{`"type`":`"task.turn_ended`",`"source`":`"antigravity`",`"task_id`":`"e2e-$run-b$_`"}" $t; if ($x.Status -eq 200) { $ok++ } }
Check '4 consecutive tasks accepted' ($ok -eq 4) "$ok/4"

# real hook path through the forwarder script (as Claude Code would call it)
$fwd = Join-Path $PSScriptRoot 'capri-signal.ps1'
$payload = "{`"session_id`":`"e2e-$run-hook`",`"cwd`":`"$($PSScriptRoot -replace '\\','\\\\')`",`"hook_event_name`":`"Stop`",`"stop_hook_active`":false,`"last_assistant_message`":`"E2E via hook forwarder.`"}"
$sw = [Diagnostics.Stopwatch]::StartNew()
$payload | powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fwd -Tool claude-code -Event Stop | Out-Null
Check 'forwarder (Claude Code Stop) exits 0' ($LASTEXITCODE -eq 0) $LASTEXITCODE
Check 'forwarder is fast (< 4 s)' ($sw.Elapsed.TotalSeconds -lt 4) $sw.Elapsed.TotalSeconds
$out = '{"conversationId":"e2e","terminationReason":"model_stop","fullyIdle":true}' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fwd -Tool antigravity -Event Stop -Reply antigravity
Check 'forwarder replies {"decision":"stop"} to Antigravity' (($out -join '') -eq '{"decision":"stop"}') ($out -join '')

# receiver not available: point the forwarder at a closed port via a fake APPDATA
$fake = Join-Path $env:TEMP ('capri-fake-' + $run)
New-Item -ItemType Directory -Path (Join-Path $fake 'Capri') -Force | Out-Null
'{"activePort":1,"token":"x"}' | Set-Content -LiteralPath (Join-Path $fake 'Capri\signal.json') -Encoding ASCII
$old = $env:APPDATA; $env:APPDATA = $fake
$sw = [Diagnostics.Stopwatch]::StartNew()
'{}' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fwd -Tool codex -Event Stop | Out-Null
$code = $LASTEXITCODE
$env:APPDATA = $old
Remove-Item -Recurse -Force $fake
Check 'receiver unavailable: forwarder still exits 0 quickly' ($code -eq 0 -and $sw.Elapsed.TotalSeconds -lt 5) "exit=$code t=$($sw.Elapsed.TotalSeconds)"

$header = "Capri Signal E2E  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  -  " + $(if ($failed -eq 0) { 'ALL PASSED' } else { "$failed FAILED" })
@($header) + $results | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'test-results.txt') -Encoding UTF8
@($header) + $results | ForEach-Object { Write-Host $_ }
exit $failed
