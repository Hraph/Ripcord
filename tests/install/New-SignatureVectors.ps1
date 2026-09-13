<#
.SYNOPSIS
    Writes the signature fixtures the installer tests verify against.

.DESCRIPTION
    A throwaway P-256 key pair, generated fresh, signing a payload that is not a release. It
    exists so the refusals — the wrong key, a flipped byte, an absent signature — are
    exercisable without the real signing key ever leaving the release workflow's secret.

    Run under PowerShell 7: it signs, and signing in the DER form needs API that Windows
    PowerShell 5.1 does not have. The tests it feeds run under both, because verifying is the
    half that has to work on a Windows Server host.

    The two awkward DER shapes are produced on purpose rather than left to chance. A DER
    integer carries a leading zero when its top bit is set, and drops leading zeros when they
    are not significant — so r and s arrive at 33, 32 or fewer bytes, and a verifier that
    assumes 32 passes its tests until the day it does not.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Destination,
    [int] $Attempts = 200
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'generating the fixtures needs PowerShell 7; verifying them does not'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$key = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

$stranger = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

try {
    $publicKeyPem = $key.ExportSubjectPublicKeyInfoPem()
    Set-Content -LiteralPath (Join-Path $Destination 'key.pub.pem') -Value $publicKeyPem

    Set-Content -LiteralPath (Join-Path $Destination 'stranger.pub.pem') `
        -Value $stranger.ExportSubjectPublicKeyInfoPem()

    $payload = [byte[]] (1..512)
    [IO.File]::WriteAllBytes((Join-Path $Destination 'payload.bin'), $payload)

    $tampered = [byte[]] $payload.Clone()
    $tampered[17] = [byte] ($tampered[17] -bxor 0x01)
    [IO.File]::WriteAllBytes((Join-Path $Destination 'tampered.bin'), $tampered)

    function Get-Signature {
        param([System.Security.Cryptography.ECDsa] $With)

        $With.SignData(
            $payload,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    }

    # The length of the first DER integer in a signature: 0x30 len 0x02 len ...
    function Get-FirstIntegerLength {
        param([byte[]] $Der)
        $at = if ($Der[1] -eq 0x81) { 4 } else { 3 }
        $Der[$at]
    }

    [IO.File]::WriteAllBytes((Join-Path $Destination 'good.sig'), (Get-Signature -With $key))
    [IO.File]::WriteAllBytes((Join-Path $Destination 'stranger.sig'), (Get-Signature -With $stranger))

    $padded = $null
    $short = $null

    for ($attempt = 0; $attempt -lt $Attempts -and (-not $padded -or -not $short); $attempt++) {
        $candidate = Get-Signature -With $key
        $length = Get-FirstIntegerLength -Der $candidate

        if (-not $padded -and $length -eq 33) { $padded = $candidate }
        if (-not $short -and $length -lt 32) { $short = $candidate }
    }

    if (-not $padded) {
        throw "no signature with a 33-byte r appeared in $Attempts attempts"
    }

    [IO.File]::WriteAllBytes((Join-Path $Destination 'padded-r.sig'), $padded)

    # An r shorter than 32 bytes needs a leading zero byte in the true value, which is one
    # chance in 256 per signature. Absent after the attempts, the fixture is skipped rather
    # than faked: a hand-built DER integer would test this script's encoder, not the installer.
    if ($short) {
        [IO.File]::WriteAllBytes((Join-Path $Destination 'short-r.sig'), $short)
    }

    Write-Host "wrote the signature fixtures to $Destination"
}
finally {
    $key.Dispose()
    $stranger.Dispose()
}
