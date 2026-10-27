# Builds YOUR Windows Capri (app/) with the classic .NET Framework compiler, exactly like the original build.ps1,
# now including app\Signal.cs. Then runs the Capri Signal self-test and rebuilds Capri-Setup.exe.
# Output: <repo>\Capri.exe, <repo>\Capri-Setup.exe, log in signal\build-windows-log.txt
param([switch]$SkipSelfTest)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $PSScriptRoot
$log = Join-Path $PSScriptRoot 'build-windows-log.txt'
"=== Capri (Windows) build $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content -LiteralPath $log -Encoding UTF8
function Log([string]$text) { $text | Add-Content -LiteralPath $log -Encoding UTF8; Write-Host $text }

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$wpf = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
if (-not (Test-Path $wpf)) { $wpf = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF" }
$winmd = "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd"
if (-not (Test-Path $winmd)) {
    $found = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\UnionMetadata" -Filter "Windows.winmd" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $winmd = $found.FullName }
}
Log "csc: $csc"
Log "winmd: $winmd (exists: $(Test-Path $winmd))"

$out = Join-Path $dir 'Capri.exe'
$running = Get-Process Capri -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $out } catch { $false } }
if ($running) { Log "ERROR: Capri.exe from this folder is running. Quit her from the tray and build again."; exit 1 }

$files = Get-ChildItem "$dir\app\*.cs" | Where-Object { $_.Name -ne 'Setup.cs' } | ForEach-Object { $_.FullName }
$res = @("/res:$dir\assets\chip.ico,Capri.Chip.ico", "/res:$dir\assets\fonts\ShareTechMono-Regular.ttf,Capri.Font.Mono.ttf")
Get-ChildItem "$dir\assets\sprites\*.png" | ForEach-Object { $res += "/res:$($_.FullName),Capri.Sprite.$($_.Name)" }
$winmdArg = if (Test-Path $winmd) { "/r:$winmd" } else { $null }
$tmp = Join-Path $dir 'Capri.new.exe'
Log ">>> Building Capri.exe ($($files.Count) files)..."
$args1 = @('/nologo', '/target:winexe', "/out:$tmp", "/win32icon:$dir\assets\chip.ico") + $res + @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Runtime.Serialization.dll', '/r:System.Xml.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:System.Runtime.dll', '/r:System.Runtime.WindowsRuntime.dll', '/r:System.Runtime.InteropServices.WindowsRuntime.dll', "/r:$wpf\WindowsBase.dll", "/r:$wpf\System.Speech.dll", "/r:$wpf\UIAutomationClient.dll", "/r:$wpf\UIAutomationTypes.dll")
if ($winmdArg) { $args1 += $winmdArg }
$args1 += $files
& $csc $args1 2>&1 | ForEach-Object { Log "$_" }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tmp)) { Log ">>> BUILD FAILED (exit $LASTEXITCODE). Capri.exe was NOT replaced."; exit 1 }
Log ">>> Build OK"

Log ">>> Running Capri Signal self-test..."
$st = Join-Path $PSScriptRoot 'selftest-windows'
if ($SkipSelfTest) { Log ">>> Self-test SKIPPED (-SkipSelfTest)." }
else {
    try { $p = Start-Process -FilePath $tmp -ArgumentList @('--signal-selftest', "`"$st`"") -PassThru -Wait }
    catch {
        $p = $null
        Log ">>> Windows didn't let the self-test run (Smart App Control). Skipping it and continuing."
    }
    if ($p) {
        Get-Content (Join-Path $st 'signal-selftest.txt') -ErrorAction SilentlyContinue | ForEach-Object { Log $_ }
        if ($p.ExitCode -ne 0) { Log ">>> SELF-TEST FAILED. Capri.exe was NOT replaced (new build kept as Capri.new.exe)."; exit 1 }
    }
}

if (Test-Path $out) { Copy-Item $out (Join-Path $dir 'Capri.previous.exe') -Force }
Move-Item $tmp $out -Force
Log ">>> Capri.exe replaced (previous copy: Capri.previous.exe)"

Log ">>> Building Capri-Setup.exe (installer with the new Capri inside)..."
& $csc /nologo /target:winexe "/out:$dir\Capri-Setup.exe" "/win32icon:$dir\assets\chip.ico" "/res:$dir\assets\chip.ico,Capri.Chip.ico" "/res:$dir\assets\chip.png,Capri.Chip.png" "/res:$out,Capri.App.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll "$dir\app\Setup.cs" "$dir\app\AssemblyInfo.cs" 2>&1 | ForEach-Object { Log "$_" }
if ($LASTEXITCODE -ne 0) { Log ">>> Capri-Setup.exe could not be built (not needed for updating)." }

# Update the installed Capri directly (no installer needed)
$installDir = Join-Path $env:LOCALAPPDATA 'Capri'
$target = Join-Path $installDir 'Capri.exe'
if (Test-Path (Join-Path $installDir '.capri-install')) {
    $open = Get-Process Capri -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $target } catch { $false } }
    if ($open) { Log ">>> Your installed Capri is open. Quit her from the tray, then run Install-CapriLocal.cmd to finish."; exit 0 }
    Copy-Item $target (Join-Path $installDir 'Capri.previous.exe') -Force -ErrorAction SilentlyContinue
    Copy-Item $out $target -Force
    Log ">>> Installed Capri updated: $target  (previous: Capri.previous.exe). Opening her..."
    try { Start-Process -FilePath $target -WorkingDirectory $installDir } catch { Log ">>> Windows blocked opening her: $($_.Exception.Message)" }
} else { Log ">>> No installed Capri found: run Capri-Setup.exe once." }
Log ">>> ALL DONE."
exit 0
