@echo off
rem Capri Signal - build the Avalonia Capri and run the headless Signal self-test.
rem Uses .NET 10 (the project's target). If only an older SDK is installed, builds a local
rem net9.0 copy WITHOUT editing the .csproj (install .NET 10 SDK to build the official target).
rem Output: signal\build-log.txt
setlocal
cd /d "%~dp0.."
set "LOG=%~dp0build-log.txt"
echo === Capri Signal build %date% %time% > "%LOG%"
where dotnet >> "%LOG%" 2>&1
dotnet --list-sdks >> "%LOG%" 2>&1
set "TFM=net10.0"
dotnet --list-sdks | findstr /b "10." >nul || set "TFM=net9.0"
echo TARGET=%TFM% >> "%LOG%"
dotnet build cross\Capri.Cross.csproj -c Release -nologo -p:TargetFramework=%TFM% "-clp:ErrorsOnly;Summary" >> "%LOG%" 2>&1
echo BUILD_EXIT=%errorlevel% >> "%LOG%"
if not exist "cross\bin\Release\%TFM%\Capri.exe" goto done
> "%~dp0run-target.txt" echo %TFM%
start "" /wait "cross\bin\Release\%TFM%\Capri.exe" --signal-selftest "%~dp0selftest"
echo SELFTEST_EXIT=%errorlevel% >> "%LOG%"
type "%~dp0selftest\signal-selftest.txt" >> "%LOG%"
:done
echo === end >> "%LOG%"
endlocal
