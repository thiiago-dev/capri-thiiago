# Capri Signal - send a fake event to the running Capri.
# Examples:
#   .\Send-CapriTest.ps1                         # Codex finished a turn
#   .\Send-CapriTest.ps1 -Type task.failed -Source claude-code -Message "Build failed"
#   .\Send-CapriTest.ps1 -Type task.needs_input -Source cursor
param(
    [ValidateSet('task.started', 'task.turn_ended', 'task.completed', 'task.needs_input', 'task.permission_required', 'task.failed')]
    [string]$Type = 'task.turn_ended',
    [string]$Source = 'codex',
    [string]$Project = 'Capri',
    [string]$Message = 'Prueba: el agente ha terminado su turno.',
    [string]$TaskId = ('test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)),
    [string]$ProjectPath = '',
    [string]$EventId = ([guid]::NewGuid().ToString('N')),
    [string]$Token = ''          # override to test a wrong token
)
$settingsPath = Join-Path $env:APPDATA 'Capri\signal.json'
if (-not (Test-Path -LiteralPath $settingsPath)) { Write-Host 'No hay signal.json: abre Capri al menos una vez.' -ForegroundColor Yellow; exit 2 }
$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$port = [int]$settings.activePort
if ($port -le 0) { Write-Host 'Capri no esta escuchando (cerrado o Signal desactivado).' -ForegroundColor Yellow; exit 3 }
if (-not $Token) { $Token = [string]$settings.token }
$body = [ordered]@{ type = $Type; source = $Source; task_id = $TaskId; event_id = $EventId; project = $Project; message = $Message }
if ($ProjectPath) { $body.project_path = $ProjectPath }
$json = $body | ConvertTo-Json -Compress
try {
    $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$port/v1/event" -Headers @{ 'X-Capri-Token' = $Token } -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 5
    Write-Host ("{0} {1}" -f [int]$r.StatusCode, $r.Content) -ForegroundColor Green
} catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'sin respuesta' }
    Write-Host "Error: $code - $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
