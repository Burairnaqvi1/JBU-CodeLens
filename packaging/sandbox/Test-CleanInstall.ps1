<#
    Runs INSIDE Windows Sandbox — a fresh Windows with no Visual Studio, no .NET runtime and no
    Visual C++ Redistributable. This is the only environment that can actually prove the
    application carries everything it needs, because the build machine has all three installed
    and cannot tell you what it is quietly relying on.

    Writes its findings to C:\out\clean-install-result.txt, which is a folder mapped back to the
    host, so the result survives the sandbox being thrown away.
#>
$ErrorActionPreference = 'Continue'
$log = 'C:\out\clean-install-result.txt'
$lines = [System.Collections.Generic.List[string]]::new()

function Say([string]$text) {
    $lines.Add($text)
    Set-Content -Path $log -Value $lines -Encoding UTF8
}

Say "JBU CodeLens - clean machine install test"
Say "started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Say ""

# --- prove the environment really is clean -------------------------------------------------
Say "ENVIRONMENT"
Say "  Windows      : $((Get-CimInstance Win32_OperatingSystem).Caption)"
foreach ($dll in 'MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'VCOMP140.DLL') {
    $present = Test-Path (Join-Path $env:SystemRoot "System32\$dll")
    Say ("  System32\{0,-20} {1}" -f $dll, $(if ($present) { 'PRESENT' } else { 'absent (as expected on a clean machine)' }))
}
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
Say "  .NET installed: $(if ($dotnet) { 'yes' } else { 'no' })"
Say ""

# --- install -------------------------------------------------------------------------------
$setup = 'C:\host-downloads\JBU-CodeLens-Setup.exe'
if (-not (Test-Path $setup)) { Say "FAIL: installer not found at $setup"; exit 1 }

Say "INSTALL"
Say "  running setup silently..."
$proc = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL' -Wait -PassThru
Say "  installer exit code: $($proc.ExitCode)"

$app = Join-Path $env:LOCALAPPDATA 'Programs\JBU CodeLens\JBU.CodeLens.UI.exe'
if (-not (Test-Path $app)) { Say "FAIL: application not installed at $app"; exit 1 }
Say "  installed to: $app"
Say ""

# --- run -----------------------------------------------------------------------------------
Say "RUN"
$run = Start-Process $app -PassThru
Say "  started, pid $($run.Id)"

# The model is around a gigabyte and is read from disk on a cold cache, so this waits
# generously rather than declaring failure early.
$deadline = (Get-Date).AddSeconds(240)
$llamaLoaded = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
    if (-not $live) { Say "  FAIL: the process exited on its own"; break }
    $live.Refresh()
    if ($live.Modules | Where-Object { $_.ModuleName -ieq 'llama.dll' }) { $llamaLoaded = $true; break }
}

$live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
if ($live) {
    Say "  process still running : yes"
    Say "  main window title     : $($live.MainWindowTitle)"
    Say ""
    Say "NATIVE LIBRARIES LOADED"
    foreach ($pattern in 'llama\.dll', 'ggml.*\.dll', 'libclang\.dll', '^(msvcp140|vcruntime140|vcruntime140_1|vcomp140)\.dll$') {
        $found = $live.Modules | Where-Object { $_.ModuleName -match $pattern }
        if ($found) {
            foreach ($m in $found) { Say ("  {0,-22} <- {1}" -f $m.ModuleName, $m.FileName) }
        }
        else {
            Say "  (nothing matching $pattern)"
        }
    }
}
else {
    Say "  process still running : NO"
}

Say ""
Say "VERDICT"
if ($llamaLoaded) {
    Say "  PASS - llama.dll loaded on a machine with no Visual C++ Redistributable installed."
    Say "  The application carries its own dependencies."
}
else {
    Say "  FAIL - llama.dll never loaded. Something the application needs is not in the package."
}

Say ""
Say "finished: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
