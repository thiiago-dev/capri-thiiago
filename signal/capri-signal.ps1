# Capri Signal - hook forwarder.
# Called by Claude Code / Codex / Cursor / Antigravity hooks. Reads the hook JSON from stdin and forwards it,
# unchanged, to Capri's local receiver (127.0.0.1 only). Never blocks the agent: 2 s timeout, always exits 0.
# It never runs anything taken from the payload.
param(
    [Parameter(Mandatory = $true)][ValidateSet('claude-code', 'codex', 'cursor', 'antigravity', 'generic')][string]$Tool,
    [string]$Event = '',
    [ValidateSet('', 'cursor', 'antigravity')][string]$Reply = ''
)
$ErrorActionPreference = 'SilentlyContinue'

function Write-Reply {
    if ($Reply -eq 'antigravity') { [Console]::Out.Write('{"decision":"stop"}') }
    elseif ($Reply -eq 'cursor') { [Console]::Out.Write('{}') }
}

try {
    [Console]::InputEncoding = [System.Text.Encoding]::UTF8
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { $raw = '{}' }

    $settingsPath = Join-Path $env:APPDATA 'Capri\signal.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) { Write-Reply; exit 0 }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $port = [int]$settings.activePort
    if ($port -le 0) { Write-Reply; exit 0 }   # Capri is closed: nothing to do

    $url = "http://127.0.0.1:$port/v1/hook/$Tool"
    if ($Event) { $url += '?event=' + [Uri]::EscapeDataString($Event) }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($raw)

    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = 'POST'
    $req.Proxy = $null
    $req.Timeout = 2000
    $req.ReadWriteTimeout = 2000
    $req.ContentType = 'application/json; charset=utf-8'
    $req.Headers.Add('X-Capri-Token', [string]$settings.token)
    $req.ContentLength = $bytes.Length
    $stream = $req.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    try { $res = $req.GetResponse(); $res.Close() } catch [System.Net.WebException] { if ($_.Exception.Response) { $_.Exception.Response.Close() } }
} catch { }
Write-Reply
exit 0
