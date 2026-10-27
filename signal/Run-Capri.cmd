@echo off
rem Starts the Avalonia Capri built by Build-And-Test.cmd.
set "TFM=net10.0"
if exist "%~dp0run-target.txt" set /p TFM=<"%~dp0run-target.txt"
start "" "%~dp0..\cross\bin\Release\%TFM%\Capri.exe"
