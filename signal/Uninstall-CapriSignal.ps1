# Capri Signal - remove the hook for one tool (preview by default, -Apply to write).
#   .\Uninstall-CapriSignal.ps1 -Tool cursor -Apply
param(
    [Parameter(Mandatory = $true)][ValidateSet('claude-code', 'codex', 'cursor', 'antigravity', 'all')][string]$Tool,
    [switch]$Apply
)
& (Join-Path $PSScriptRoot 'Install-CapriSignal.ps1') -Tool $Tool -Uninstall -Apply:$Apply
