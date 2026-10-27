# Capri Signal - install / uninstall the hook for one tool, in YOUR user config.
# Dry run by default: it prints what would change. Nothing is written unless you add -Apply.
# A timestamped backup of the original file is saved next to it before writing.
#
#   .\Install-CapriSignal.ps1 -Tool claude-code             # preview
#   .\Install-CapriSignal.ps1 -Tool claude-code -Apply      # install
#   .\Install-CapriSignal.ps1 -Tool claude-code -Uninstall -Apply
#   .\Install-CapriSignal.ps1 -Tool all -Status             # what is installed where
param(
    [Parameter(Mandatory = $true)][ValidateSet('claude-code', 'codex', 'cursor', 'antigravity', 'all')][string]$Tool,
    [switch]$Apply,
    [switch]$Uninstall,
    [switch]$Status
)
$ErrorActionPreference = 'Stop'
$Forwarder = Join-Path $PSScriptRoot 'capri-signal.ps1'
if (-not (Test-Path -LiteralPath $Forwarder)) { throw "No encuentro $Forwarder" }
$Marker = 'capri-signal.ps1'
$PsPrefix = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File'

function Get-ConfigPath([string]$t) {
    switch ($t) {
        'claude-code' { return Join-Path $HOME '.claude\settings.json' }
        'codex' { return Join-Path $HOME '.codex\hooks.json' }
        'cursor' { return Join-Path $HOME '.cursor\hooks.json' }
        'antigravity' { return Join-Path $HOME '.gemini\config\hooks.json' }
    }
}

function Read-Json([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { throw "$path no es JSON valido (quiza tiene comentarios). Editalo a mano; no lo toco." }
}

function Set-Prop($obj, [string]$name, $value) {
    if ($obj.PSObject.Properties[$name]) { $obj.$name = $value } else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

function Get-Prop($obj, [string]$name) { if ($obj -and $obj.PSObject.Properties[$name]) { return $obj.$name } return $null }

function Test-Ours($handler) {
    $text = (([string](Get-Prop $handler 'command')) + ' ' + ((@(Get-Prop $handler 'args')) -join ' '))
    return $text -like "*$Marker*"
}

# ---- tool-specific handlers ---------------------------------------------------------------------
function New-ClaudeHandler([string]$event) {
    [pscustomobject][ordered]@{
        type    = 'command'
        command = 'powershell.exe'
        args    = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $Forwarder, '-Tool', 'claude-code', '-Event', $event)
        timeout = 10
    }
}
function New-CodexHandler([string]$event) {
    [pscustomobject][ordered]@{ type = 'command'; command = "$PsPrefix `"$Forwarder`" -Tool codex -Event $event"; timeout = 10 }
}

# Claude Code & Codex share the same layout: { hooks: { Event: [ { matcher?, hooks: [handler] } ] } }
function Add-Grouped($root, [string]$event, [string]$matcher, $handler) {
    if (-not (Get-Prop $root 'hooks')) { Set-Prop $root 'hooks' ([pscustomobject]@{}) }
    $groups = @(Get-Prop $root.hooks $event | Where-Object { $_ })
    $group = [pscustomobject][ordered]@{}
    if ($matcher) { Set-Prop $group 'matcher' $matcher }
    Set-Prop $group 'hooks' @($handler)
    Set-Prop $root.hooks $event (@($groups) + $group)
}

function Remove-Grouped($root) {
    $hooks = Get-Prop $root 'hooks'
    if (-not $hooks) { return 0 }
    $removed = 0
    foreach ($p in @($hooks.PSObject.Properties)) {
        $kept = @()
        foreach ($group in @($p.Value)) {
            $handlers = @(Get-Prop $group 'hooks')
            $mine = @($handlers | Where-Object { Test-Ours $_ })
            $removed += $mine.Count
            $rest = @($handlers | Where-Object { -not (Test-Ours $_) })
            if ($mine.Count -eq 0) { $kept += $group }
            elseif ($rest.Count -gt 0) { Set-Prop $group 'hooks' $rest; $kept += $group }
        }
        if ($kept.Count -eq 0) { $hooks.PSObject.Properties.Remove($p.Name) } else { Set-Prop $hooks $p.Name $kept }
    }
    if (@($hooks.PSObject.Properties).Count -eq 0) { $root.PSObject.Properties.Remove('hooks') }
    return $removed
}

function Update-Config([string]$t, $root, [bool]$remove) {
    if ($null -eq $root) { $root = [pscustomobject]@{} }
    switch ($t) {
        'claude-code' {
            $n = Remove-Grouped $root
            if (-not $remove) {
                Add-Grouped $root 'UserPromptSubmit' $null (New-ClaudeHandler 'UserPromptSubmit')
                Add-Grouped $root 'Stop' $null (New-ClaudeHandler 'Stop')
                Add-Grouped $root 'StopFailure' $null (New-ClaudeHandler 'StopFailure')
                Add-Grouped $root 'Notification' 'permission_prompt|idle_prompt|agent_needs_input|agent_completed|elicitation_dialog|elicitation_url_dialog' (New-ClaudeHandler 'Notification')
            }
        }
        'codex' {
            $n = Remove-Grouped $root
            if (-not $remove) {
                Add-Grouped $root 'UserPromptSubmit' $null (New-CodexHandler 'UserPromptSubmit')
                Add-Grouped $root 'Stop' $null (New-CodexHandler 'Stop')
                Add-Grouped $root 'PermissionRequest' $null (New-CodexHandler 'PermissionRequest')
            }
        }
        'cursor' {
            if (-not (Get-Prop $root 'version')) { Set-Prop $root 'version' 1 }
            if (-not (Get-Prop $root 'hooks')) { Set-Prop $root 'hooks' ([pscustomobject]@{}) }
            $stop = @(Get-Prop $root.hooks 'stop' | Where-Object { $_ -and -not (Test-Ours $_) })
            if (-not $remove) { $stop += [pscustomobject][ordered]@{ command = "$PsPrefix `"$Forwarder`" -Tool cursor -Event stop -Reply cursor"; timeout = 10 } }
            if ($stop.Count -gt 0) { Set-Prop $root.hooks 'stop' $stop } else { $root.hooks.PSObject.Properties.Remove('stop') }
        }
        'antigravity' {
            $root.PSObject.Properties.Remove('capri-signal')
            if (-not $remove) {
                $fwd = $Forwarder -replace '\\', '/'
                Set-Prop $root 'capri-signal' ([pscustomobject][ordered]@{
                        enabled = $true
                        Stop    = @([pscustomobject][ordered]@{ type = 'command'; command = "$PsPrefix `"$fwd`" -Tool antigravity -Event Stop -Reply antigravity"; timeout = 10 })
                    })
            }
        }
    }
    return $root
}

function Show-Status([string]$t) {
    $path = Get-ConfigPath $t
    $text = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw } else { '' }
    $state = if ($text -like "*$Marker*") { 'INSTALADO' } elseif ($text) { 'no instalado' } else { 'no instalado (no existe el archivo)' }
    '{0,-12} {1,-36} {2}' -f $t, $state, $path
}

$tools = if ($Tool -eq 'all') { @('claude-code', 'codex', 'cursor', 'antigravity') } else { @($Tool) }
if ($Status) { $tools | ForEach-Object { Show-Status $_ }; exit 0 }

foreach ($t in $tools) {
    $path = Get-ConfigPath $t
    $before = Read-Json $path
    $beforeText = if ($before) { $before | ConvertTo-Json -Depth 50 } else { '(no existe)' }
    $copy = if ($before) { $beforeText | ConvertFrom-Json } else { $null }
    $after = Update-Config $t $copy ([bool]$Uninstall)
    $afterText = $after | ConvertTo-Json -Depth 50

    Write-Host ''
    Write-Host ("=== {0}: {1} ===" -f $t, $(if ($Uninstall) { 'desinstalar' } else { 'instalar' })) -ForegroundColor Cyan
    Write-Host "Archivo: $path"
    if ($beforeText -eq $afterText) { Write-Host 'Sin cambios.' -ForegroundColor DarkGray; continue }
    Write-Host '--- resultado ---' -ForegroundColor DarkGray
    Write-Host $afterText
    if (-not $Apply) { Write-Host 'Vista previa: no se ha escrito nada. Repite con -Apply para aplicarlo.' -ForegroundColor Yellow; continue }

    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    if (Test-Path -LiteralPath $path) {
        $backup = "$path.capri-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Copy-Item -LiteralPath $path -Destination $backup
        Write-Host "Copia de seguridad: $backup" -ForegroundColor DarkGray
    }
    [System.IO.File]::WriteAllText($path, $afterText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'Guardado.' -ForegroundColor Green
    if ($t -eq 'codex') { Write-Host 'Codex: si en ~/.codex/config.toml tienes [features] hooks = false, los hooks no se ejecutaran.' -ForegroundColor DarkGray }
    if ($t -eq 'claude-code') { Write-Host 'Claude Code: reinicia las sesiones abiertas (o revisa /hooks) para que cargue el cambio.' -ForegroundColor DarkGray }
}
