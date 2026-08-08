<#
.SYNOPSIS
    Issues serial numbers for JBU CodeLens, and checks existing ones.

.DESCRIPTION
    Implements the same check-character scheme as CheckSerial in
    JBU.CodeLens.iss. A key is JBUC-XXXXX-XXXXX-XXXXX: fourteen characters
    chosen at random, then a fifteenth worked out from them.

    Keep the two implementations in step. If the alphabet or the weighting
    changes here it must change in the installer script as well, or keys issued
    by this script will be rejected by setup.

.EXAMPLE
    .\New-SerialKey.ps1 -Count 5
    Issues five keys.

.EXAMPLE
    .\New-SerialKey.ps1 -Verify JBUC-4K7P2-9WX3M-QT58R
    Reports whether a key would be accepted.
#>
[CmdletBinding(DefaultParameterSetName = 'Generate')]
param(
    [Parameter(ParameterSetName = 'Generate')]
    [ValidateRange(1, 100)]
    [int]$Count = 1,

    [Parameter(ParameterSetName = 'Verify', Mandatory)]
    [string]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 0/O and 1/I/L are left out so a key copied off paper cannot be mistyped
# through characters that look alike.
$Alphabet = '23456789ABCDEFGHJKMNPQRSTUVWXYZ'
$Prefix = 'JBUC'

function Get-CheckCharacter {
    param([string]$Body14)

    $weighted = 0
    for ($i = 0; $i -lt 14; $i++) {
        $index = $Alphabet.IndexOf($Body14[$i])
        if ($index -lt 0) { throw "Character '$($Body14[$i])' is not in the alphabet." }
        # Weighted by position so a transposition breaks the sum.
        $weighted += $index * ($i + 1)
    }

    return $Alphabet[$weighted % $Alphabet.Length]
}

function Format-Key {
    param([string]$Body15)
    return "$Prefix-$($Body15.Substring(0,5))-$($Body15.Substring(5,5))-$($Body15.Substring(10,5))"
}

function Test-Key {
    param([string]$Key)

    $entered = $Key.Trim().ToUpperInvariant()
    if ($entered.Length -ne 22) { return $false }
    if ($entered.Substring(0, 4) -ne $Prefix) { return $false }
    if ($entered[4] -ne '-' -or $entered[10] -ne '-' -or $entered[16] -ne '-') { return $false }

    $body = $entered.Substring(5, 5) + $entered.Substring(11, 5) + $entered.Substring(17, 5)
    foreach ($c in $body.ToCharArray()) {
        if ($Alphabet.IndexOf($c) -lt 0) { return $false }
    }

    return $body[14] -eq (Get-CheckCharacter -Body14 $body.Substring(0, 14))
}

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    $ok = Test-Key -Key $Verify
    [pscustomobject]@{ Serial = $Verify; Valid = $ok }
    return
}

# RandomNumberGenerator rather than Get-Random: keys should not be predictable
# from the time the script happened to run.
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    for ($n = 0; $n -lt $Count; $n++) {
        $body = ''
        $bytes = [byte[]]::new(14)
        $rng.GetBytes($bytes)
        for ($i = 0; $i -lt 14; $i++) {
            $body += $Alphabet[$bytes[$i] % $Alphabet.Length]
        }

        $body += Get-CheckCharacter -Body14 $body
        Format-Key -Body15 $body
    }
}
finally {
    $rng.Dispose()
}
