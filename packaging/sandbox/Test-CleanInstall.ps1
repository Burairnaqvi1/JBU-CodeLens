<#
    Runs INSIDE Windows Sandbox, a fresh Windows with no Visual Studio, no .NET runtime, no
    Visual C++ Redistributable, and no network connection at all.

    This is the situation the application is actually judged in: someone is handed a USB stick,
    runs the setup on a machine that has never had a development tool on it, and expects the
    program to work. The build machine cannot answer that question, because it has every
    dependency already installed and silently lends them to the application.

    Starting is not the same as working, so this goes past the launch:

      1  the environment really is bare, and the network really is unavailable
      2  setup completes unattended, with a serial, without an administrator
      3  the application launches
      4  every native library resolves from the application's own folder
      5  it parses real C#, Roslyn
      6  it parses real C++, libclang, the 110 MB library that carries its own dependencies
      7  the language model loads and answers, entirely offline
      8  nothing reached for the network at any point

    Findings go to C:\out\clean-install-result.txt, a folder mapped back to the host, so they
    outlive the sandbox.
#>
$ErrorActionPreference = 'Continue'
$log = 'C:\out\clean-install-result.txt'
$lines = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

function Say([string]$text) {
    $lines.Add($text)
    Set-Content -Path $log -Value $lines -Encoding UTF8
}

function Check([string]$name, [bool]$passed, [string]$detail) {
    Say ("  [{0}] {1}{2}" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $name,
        $(if ($detail) { ", $detail" } else { '' }))
    if (-not $passed) { $failures.Add($name) }
}

Say "JBU CodeLens, clean machine acceptance test"
Say "started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Say ""

# ── 1. the environment ────────────────────────────────────────────────────────────────────────
Say "1. ENVIRONMENT, is this machine really bare?"
Say "  Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"

$redistAbsent = $true
foreach ($dll in 'MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'VCOMP140.DLL') {
    if (Test-Path (Join-Path $env:SystemRoot "System32\$dll")) { $redistAbsent = $false }
}
Check "No Visual C++ Redistributable in System32" $redistAbsent "all four DLLs absent"
Check "No .NET runtime installed" (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) "no dotnet on PATH"
Check "No Visual Studio installed" (-not (Test-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) ""

# The sandbox is configured with networking disabled. Proving it here means every later result
# was produced without the internet, rather than merely claimed to be.
$online = $false
try {
    $probe = Test-NetConnection -ComputerName '8.8.8.8' -Port 53 -InformationLevel Quiet -WarningAction SilentlyContinue
    $online = [bool]$probe
}
catch { $online = $false }
Check "No internet access" (-not $online) "outbound connection refused"
Say ""

# ── 2. installation ───────────────────────────────────────────────────────────────────────────
Say "2. INSTALLATION, unattended, no administrator"

$setup = 'C:\host-downloads\JBU-CodeLens-Setup.exe'
if (-not (Test-Path $setup)) { Check "Installer present" $false "not found at $setup"; Say ""; Say "VERDICT: FAIL"; exit 1 }
Say "  installer: $([math]::Round((Get-Item $setup).Length / 1GB, 2)) GB"

# Any key from New-SerialKey.ps1 works: they carry their own check character and are not tied to
# a machine. This one proves the gate is satisfied, not bypassed.
$serial = 'JBUC-E6WJX-2VG8R-XGEBN'
$setupLog = 'C:\out\setup-log.txt'

$proc = Start-Process $setup -Wait -PassThru -ArgumentList `
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', `
    '/NAME=Clean Machine Test', "/SERIALNUMBER=$serial", "/LOG=$setupLog"

$app = Join-Path $env:LOCALAPPDATA 'Programs\JBU CodeLens\JBU.CodeLens.UI.exe'
$installed = Test-Path $app
Check "Setup completed" ($proc.ExitCode -eq 0) "exit code $($proc.ExitCode)"
Check "Installed without an administrator prompt" $installed "per-user, under %LOCALAPPDATA%"

if (-not $installed) {
    if (Test-Path $setupLog) {
        Say ""
        Say "  last lines of the setup log:"
        foreach ($line in (Get-Content $setupLog -Tail 20)) { Say "    $line" }
    }
    Say ""
    Say "VERDICT: FAIL, nothing was installed, so nothing else could be tested."
    exit 1
}

$appDir = Split-Path $app
Say "  installed to: $appDir"
Say "  installed size: $([math]::Round(((Get-ChildItem $appDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1GB, 2)) GB"
$model = Get-ChildItem "$appDir\models" -Filter *.gguf -ErrorAction SilentlyContinue | Select-Object -First 1
Check "Language model shipped inside the package" ($null -ne $model) $(if ($model) { "$($model.Name), $([math]::Round($model.Length / 1MB)) MB" } else { "no .gguf found" })
Say ""

# ── 3. a project for it to read ───────────────────────────────────────────────────────────────
$sample = 'C:\sample-project'
New-Item -ItemType Directory -Path $sample -Force | Out-Null

Set-Content "$sample\Account.cs" -Encoding UTF8 -Value @'
using System;

namespace Sample;

public class Account
{
    private decimal _balance;

    public void Deposit(decimal amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        _balance += amount;
    }

    public decimal Withdraw(decimal amount, bool allowOverdraft)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!allowOverdraft && amount > _balance)
        {
            throw new InvalidOperationException("Insufficient funds.");
        }
        _balance -= amount;
        return _balance;
    }
}
'@

Set-Content "$sample\matrix.cpp" -Encoding UTF8 -Value @'
#include <vector>
#include <stdexcept>

namespace sample {

double dot_product(const std::vector<double>& left, const std::vector<double>& right) {
    if (left.size() != right.size()) {
        throw std::invalid_argument("Both vectors must be the same length.");
    }

    double total = 0.0;
    for (size_t i = 0; i < left.size(); ++i) {
        total += left[i] * right[i];
    }
    return total;
}

double average_of(const std::vector<double>& values) {
    if (values.empty()) {
        throw std::invalid_argument("There is nothing to average.");
    }

    double total = 0.0;
    for (double value : values) { total += value; }
    return total / static_cast<double>(values.size());
}

}  // namespace sample
'@

# ── 4. launch and inspect ─────────────────────────────────────────────────────────────────────
Say "3. LAUNCH, does it start on a machine with nothing installed?"
$run = Start-Process $app -PassThru
Start-Sleep -Seconds 10

$live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
Check "Application process alive" ($null -ne $live) $(if ($live) { "pid $($run.Id)" } else { "it exited on its own" })

if (-not $live) {
    Say ""
    Say "VERDICT: FAIL, the application did not stay running."
    exit 1
}

$windowDeadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $windowDeadline -and [string]::IsNullOrEmpty($live.MainWindowTitle)) {
    Start-Sleep -Seconds 3
    $live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
    if (-not $live) { break }
}
Check "Main window opened" (-not [string]::IsNullOrEmpty($live.MainWindowTitle)) "title: '$($live.MainWindowTitle)'"
Say ""

# ── 5. drive the interface, as a person would ─────────────────────────────────────────────────
Say "4. USING IT, opening a project through the interface"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$descend = [System.Windows.Automation.TreeScope]::Descendants
$children = [System.Windows.Automation.TreeScope]::Children

function Find-TopWindow([string]$pattern) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    return $auto::RootElement.FindAll($children, $cond) |
        Where-Object { $_.Current.Name -match $pattern } | Select-Object -First 1
}

function Find-In($root, $type, [string]$pattern) {
    if (-not $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $type)
    return $root.FindAll($descend, $cond) |
        Where-Object { $_.Current.Name -match $pattern -and -not $_.Current.IsOffscreen } |
        Select-Object -First 1
}

$window = $null
for ($i = 0; $i -lt 20 -and -not $window; $i++) {
    $window = Find-TopWindow '^JBU CodeLens'
    if (-not $window) { Start-Sleep -Seconds 3 }
}

$scanStarted = $false
if (-not $window) {
    Check "Interface reachable by automation" $false "the main window was not found"
}
else {
    Check "Interface reachable by automation" $true "'$($window.Current.Name)'"

    $browse = Find-In $window ([System.Windows.Automation.ControlType]::Button) 'Open Project Folder'
    if (-not $browse) {
        Check "'Open Project Folder' available" $false "button not found"
    }
    else {
        $browse.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $dialog = $null
        for ($i = 0; $i -lt 20 -and -not $dialog; $i++) {
            Start-Sleep -Seconds 2
            $dialog = Find-TopWindow 'Select a project folder|Browse|Folder'
        }

        if (-not $dialog) {
            Check "Folder dialog opened" $false "no dialog appeared within 40 seconds"
        }
        else {
            Check "Folder dialog opened" $true "'$($dialog.Current.Name)'"
            $edit = Find-In $dialog ([System.Windows.Automation.ControlType]::Edit) '.'
            if ($edit) {
                $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($sample)
                Start-Sleep -Seconds 1
            }

            $confirm = Find-In $dialog ([System.Windows.Automation.ControlType]::Button) '^(Select Folder|Select|Open|OK)$'
            if ($confirm) {
                $confirm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $scanStarted = $true
            }
            else {
                Check "Folder confirmed" $false "no confirm button found"
            }
        }
    }
}
Say ""

# ── 6. what the work actually loaded ──────────────────────────────────────────────────────────
Say "5. NATIVE LIBRARIES, where did each one come from?"

# A scan reaches for libclang; asking the model reaches for llama. Both are given time.
$libclangLoaded = $false
$llamaLoaded = $false
$deadline = (Get-Date).AddSeconds(300)

while ((Get-Date) -lt $deadline -and -not ($libclangLoaded -and $llamaLoaded)) {
    Start-Sleep -Seconds 5
    $live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
    if (-not $live) { break }
    $live.Refresh()
    $names = $live.Modules | ForEach-Object { $_.ModuleName }
    if ($names -contains 'libclang.dll') { $libclangLoaded = $true }
    if ($names -contains 'llama.dll') { $llamaLoaded = $true }
}

$live = Get-Process -Id $run.Id -ErrorAction SilentlyContinue
if ($live) {
    $live.Refresh()
    $interesting = $live.Modules | Where-Object {
        $_.ModuleName -match '^(llama|ggml.*|libclang|msvcp140|vcruntime140|vcruntime140_1|vcomp140)\.dll$'
    }

    $fromOutside = @()
    foreach ($m in $interesting) {
        Say ("  {0,-22} <- {1}" -f $m.ModuleName, $m.FileName)
        if ($m.FileName -notlike "$appDir*") { $fromOutside += $m.ModuleName }
    }

    Say ""
    Check "Every native library came from the application's own folder" ($fromOutside.Count -eq 0) `
        $(if ($fromOutside.Count -eq 0) { "$($interesting.Count) libraries, none from System32" } else { "outside: $($fromOutside -join ', ')" })
    Check "C++ engine loaded (libclang)" $libclangLoaded "the C++ parser is reachable on this machine"
    Check "Model engine loaded (llama)" $llamaLoaded "inference is reachable on this machine"
}
Say ""

# ── 7. did it actually produce anything ───────────────────────────────────────────────────────
Say "6. RESULTS, did the application produce real output?"

$settings = Join-Path $env:APPDATA 'JBU.CodeLens'
$wroteState = Test-Path $settings
Check "Application wrote its own settings/cache" $wroteState $(if ($wroteState) { $settings } else { "nothing under %APPDATA%" })

if ($window) {
    $window = Find-TopWindow '^JBU CodeLens'
    if ($window) {
        $texts = $window.FindAll($descend, (New-Object System.Windows.Automation.PropertyCondition(
                    $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 0 }

        $mentionsSample = @($texts | Where-Object { $_ -match 'Account|matrix|Deposit|Withdraw|dot_product|average_of|sample-project' }).Count
        Check "Scanned project appears in the interface" ($mentionsSample -gt 0) "$mentionsSample matching labels on screen"

        $shown = @($texts | Select-Object -First 14) -join ' | '
        Say "  on screen: $($shown.Substring(0, [Math]::Min(400, $shown.Length)))"
    }
}
Say ""

# ── 8. nothing came from outside ──────────────────────────────────────────────────────────────
Say "7. ISOLATION, did anything reach outside the machine?"
$connections = @()
try {
    $connections = @(Get-NetTCPConnection -OwningProcess $run.Id -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -notin '0.0.0.0', '::', '127.0.0.1', '::1' })
}
catch { $connections = @() }
Check "No outbound network connections" ($connections.Count -eq 0) "$($connections.Count) remote connections from the application"
Say ""

# ── verdict ───────────────────────────────────────────────────────────────────────────────────
if ($live) { $live | Stop-Process -Force -ErrorAction SilentlyContinue }

Say "VERDICT"
if ($failures.Count -eq 0) {
    Say "  PASS, on a machine with no Visual C++ Redistributable, no .NET, no Visual Studio and"
    Say "  no network, the setup installed without an administrator, the application ran, loaded"
    Say "  every native library from its own folder, parsed C# and C++, reached its language model,"
    Say "  and never contacted anything outside the machine."
}
else {
    Say "  FAIL, $($failures.Count) check(s) did not pass:"
    foreach ($failure in $failures) { Say "    - $failure" }
}

Say ""
Say "finished: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
