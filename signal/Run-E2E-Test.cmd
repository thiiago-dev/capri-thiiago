@echo off
rem Runs the end-to-end test against the running Capri. Results: signal\test-results.txt
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-CapriSignal.ps1"
