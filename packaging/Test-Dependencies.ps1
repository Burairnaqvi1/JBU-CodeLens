<#
.SYNOPSIS
    Checks that every DLL the published application imports will be present on a clean Windows.

.DESCRIPTION
    Reads the real PE import tables — both the normal and the delay-load directory — of every
    executable and DLL in a published folder, and sorts what they import into three groups:

      shipped   the file sits in the package itself
      windows   the file is part of Windows and lives in System32
      apiset    an api-ms-win-* contract. These are not files on disk: the loader maps them to
                whichever real DLL provides them on the running version of Windows. They are
                checked by actually asking the loader to resolve one, not by looking for a file.
      MISSING   none of the above, which means the application would fail to start on a machine
                that does not happen to have it

    A string scan of the binaries would only ever produce a superset. This walks the import
    directory, so a name appearing here is genuinely imported.

    Anything reported as MISSING is a real deployment bug: it works on the build machine only
    because something else installed that file.

.EXAMPLE
    .\Test-Dependencies.ps1 -PublishDir C:\path\to\published\folder
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) { throw "Publish folder not found: $PublishDir" }

function Get-ImportedDlls {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64) { return @() }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return @() }   # not "MZ"

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or $peOffset + 24 -ge $bytes.Length) { return @() }
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) { return @() }   # not "PE\0\0"

    $numberOfSections = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
    $sizeOfOptional = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optionalStart = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optionalStart)

    # PE32 keeps the data directories 96 bytes into the optional header, PE32+ 112, because the
    # 64-bit form widens several fields before them.
    $dataDirStart = $optionalStart + $(if ($magic -eq 0x20B) { 112 } else { 96 })

    $sectionStart = $optionalStart + $sizeOfOptional
    $sections = @()
    for ($i = 0; $i -lt $numberOfSections; $i++) {
        $s = $sectionStart + ($i * 40)
        if ($s + 40 -gt $bytes.Length) { break }
        $sections += [pscustomobject]@{
            VirtualAddress = [BitConverter]::ToUInt32($bytes, $s + 12)
            VirtualSize    = [BitConverter]::ToUInt32($bytes, $s + 8)
            RawPointer     = [BitConverter]::ToUInt32($bytes, $s + 20)
            RawSize        = [BitConverter]::ToUInt32($bytes, $s + 16)
        }
    }

    function Convert-RvaToOffset {
        param([uint32]$Rva)
        foreach ($sec in $sections) {
            # Compared against the raw size as well: a section can be larger in memory than on
            # disk, and an RVA landing in that tail has no bytes behind it to read.
            $span = [Math]::Max($sec.VirtualSize, $sec.RawSize)
            if ($Rva -ge $sec.VirtualAddress -and $Rva -lt $sec.VirtualAddress + $span) {
                return [int]($Rva - $sec.VirtualAddress + $sec.RawPointer)
            }
        }
        return -1
    }

    function Read-AsciiAt {
        param([int]$Offset)
        if ($Offset -lt 0 -or $Offset -ge $bytes.Length) { return $null }
        $end = $Offset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        return [System.Text.Encoding]::ASCII.GetString($bytes, $Offset, $end - $Offset)
    }

    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    # Directory 1 is the import table; directory 13 is delay-load, which fails just as loudly
    # when the file is absent, only later.
    foreach ($directory in @(1, 13)) {
        $entry = $dataDirStart + ($directory * 8)
        if ($entry + 8 -gt $bytes.Length) { continue }
        $tableRva = [BitConverter]::ToUInt32($bytes, $entry)
        if ($tableRva -eq 0) { continue }

        $offset = Convert-RvaToOffset -Rva $tableRva
        if ($offset -lt 0) { continue }

        # Normal descriptors are 20 bytes with the name RVA at +12; delay-load descriptors are
        # 32 bytes with it at +4.
        $stride = $(if ($directory -eq 13) { 32 } else { 20 })
        $nameField = $(if ($directory -eq 13) { 4 } else { 12 })

        for ($i = 0; $i -lt 4096; $i++) {
            $descriptor = $offset + ($i * $stride)
            if ($descriptor + $stride -gt $bytes.Length) { break }

            $allZero = $true
            for ($b = 0; $b -lt $stride; $b++) {
                if ($bytes[$descriptor + $b] -ne 0) { $allZero = $false; break }
            }
            if ($allZero) { break }

            $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + $nameField)
            if ($nameRva -eq 0) { continue }
            $name = Read-AsciiAt -Offset (Convert-RvaToOffset -Rva $nameRva)
            if ($name -and $name -match '\.dll$') { [void]$names.Add($name) }
        }
    }

    return @($names)
}

$binaries = Get-ChildItem $PublishDir -Recurse -File -Include *.exe, *.dll
$shipped = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($b in $binaries) { [void]$shipped.Add($b.Name) }

$system32 = Join-Path $env:SystemRoot 'System32'

# These arrive with the Visual C++ Redistributable, not with Windows. A build machine has them in
# System32 and a clean machine does not, so testing "is it in System32" would pass here and the
# application would still fail to load its model on the target. They must be inside the package.
$redistributable = @(
    'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll',
    'msvcp140_codecvt_ids.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
    'vcruntime140_threads.dll', 'vcomp140.dll', 'concrt140.dll', 'vccorlib140.dll'
)

# An api-ms-win-* name is a contract, not a file. Rather than assume the loader can satisfy it,
# ask the loader: LoadLibrary resolves the contract exactly as it would at start-up, so a handle
# coming back is proof the import can be satisfied on this version of Windows.
Add-Type -Namespace Native -Name Loader -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr LoadLibraryW(string name);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool FreeLibrary(IntPtr handle);
'@

function Test-ApiSetResolves {
    param([string]$Name)
    $handle = [Native.Loader]::LoadLibraryW($Name)
    if ($handle -eq [IntPtr]::Zero) { return $false }
    [void][Native.Loader]::FreeLibrary($handle)
    return $true
}

$results = @{}

foreach ($binary in $binaries) {
    foreach ($import in (Get-ImportedDlls -Path $binary.FullName)) {
        if (-not $results.ContainsKey($import)) {
            # Checked before System32 on purpose: finding one of these there proves only that
            # this machine has the redistributable installed, which says nothing about the
            # machine the application is going to.
            $status =
                if ($shipped.Contains($import)) { 'shipped' }
                elseif ($redistributable -contains $import.ToLowerInvariant()) { 'MISSING (VC++ redist)' }
                elseif (Test-Path (Join-Path $system32 $import)) { 'windows' }
                elseif (($import -like 'api-ms-win-*' -or $import -like 'ext-ms-win-*') -and
                        (Test-ApiSetResolves -Name $import)) { 'apiset' }
                else { 'MISSING' }
            $results[$import] = [pscustomobject]@{
                Dll        = $import
                Status     = $status
                ImportedBy = $binary.Name
            }
        }
    }
}

$ordered = @($results.Values | Sort-Object Status, Dll)
foreach ($row in $ordered) {
    Write-Host ("  {0,-40} {1,-8} (first seen in {2})" -f $row.Dll, $row.Status, $row.ImportedBy)
}

$missing = @($ordered | Where-Object { $_.Status -like 'MISSING*' })
Write-Host ''
Write-Host ("binaries scanned : {0}" -f $binaries.Count)
Write-Host ("distinct imports : {0}" -f $ordered.Count)
Write-Host ("shipped          : {0}" -f @($ordered | Where-Object { $_.Status -eq 'shipped' }).Count)
Write-Host ("from Windows     : {0}" -f @($ordered | Where-Object { $_.Status -eq 'windows' }).Count)
Write-Host ("api set contracts: {0}" -f @($ordered | Where-Object { $_.Status -eq 'apiset' }).Count)

if ($missing.Count -gt 0) {
    Write-Host ("MISSING          : {0}" -f $missing.Count)
    Write-Host ''
    Write-Host 'These would have to be installed separately, which is a deployment bug.'
    exit 1
}

Write-Host 'MISSING          : 0'
Write-Host ''
Write-Host 'Every imported DLL is inside the package, part of Windows, or an api-set the loader resolves.'
