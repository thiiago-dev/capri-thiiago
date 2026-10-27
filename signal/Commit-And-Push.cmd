@echo off
rem Capri Signal - commit the work to branch "capri-signal" and push it to GitHub.
setlocal
cd /d "%~dp0.."
set "LOG=%~dp0push-log.txt"
echo === %date% %time% > "%LOG%"
git checkout -B capri-signal >> "%LOG%" 2>&1
git add .gitignore cross/Pet.cs cross/Program.cs cross/Signal signal >> "%LOG%" 2>&1
git commit -m "Add Capri Signal: local agent notifications for the Avalonia build" -m "Local 127.0.0.1 receiver with token auth, adapters for Claude Code, Codex, Cursor and Antigravity hooks, dedup queue, provisional Avalonia cards, synthesized ring, preferences, self-test and PowerShell install/test scripts." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -m "Claude-Session: https://claude.ai/code/session_01N9TpJ7QzP6gYmtARitAKvi" >> "%LOG%" 2>&1
git push -u origin capri-signal >> "%LOG%" 2>&1
echo PUSH_EXIT=%errorlevel% >> "%LOG%"
git log --oneline -1 >> "%LOG%" 2>&1
type "%LOG%"
pause
