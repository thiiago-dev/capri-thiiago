@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-CapriWindows.ps1" -SkipSelfTest
pause
