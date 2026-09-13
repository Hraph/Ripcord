<#
.SYNOPSIS
    Installs Ripcord on this host, verifying the release before it lands.

.DESCRIPTION
    The first install. Subsequent ones are `ripcord update`, which is a different operation
    with different safety properties — this one refuses to touch a host that already has a
    binary unless told twice.

    It downloads the release, checks the SHA-256 and verifies the detached ECDSA P-256
    signature against a public key written into this script, and stops without installing
    anything if either fails. There is no switch to skip that: a switch to skip verification
    is the switch somebody uses at 3 a.m.

    The two hosts this tool is for are meant to have no outbound access. `-Prepare` downloads
    and verifies on a machine that does have it; `-FromPath` installs from the folder that
    produced, with the same verification — a folder that arrived on a USB stick is not more
    trusted than a download.

    Never run against a Hyper-V host from an untrusted source: this script is not itself
    signed. What it installs is.

.EXAMPLE
    irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1))) -Prepare D:\ripcord-release
#>

[CmdletBinding()]
param(
    # Which sample configuration to place beside the binary. Asked for if omitted and the host
    # has none.
    [ValidateSet('primary', 'dr')]
    [string] $Role,

    [string] $Path = "$env:ProgramFiles\Ripcord",

    # A specific release tag, such as v0.1.0. The latest one otherwise.
    [string] $Version,

    # Download and verify into this folder, and install nothing. For the machine with network.
    [string] $Prepare,

    # Install from a folder already holding ripcord.exe, ripcord.exe.sha256 and ripcord.exe.sig.
    [string] $FromPath,

    # Create the scheduled task that runs `ripcord check --notify` every fifteen minutes.
    [switch] $CheckTask,

    # Put a Start Menu shortcut to the dashboard page beside the rest.
    [switch] $Shortcut,

    # Replace an existing ripcord.exe. Never replaces an existing ripcord.yaml.
    [switch] $Force,

    # Load the functions without doing anything, so the tests can reach them.
    [switch] $SourceOnly
)

$ErrorActionPreference = 'Stop'

# The repository by numeric id, never by owner and name. A rename leaves a redirect that keeps
# working right up to the moment somebody creates a repository under the abandoned name — at
# which point the old URL stops failing and starts answering, successfully, with a stranger's
# releases. The id is immutable and inert against that. The name appears once, in the URL a
# human types to fetch this file, where an id nobody can read would be worse.
$script:RepositoryId = 1367653231

$script:ExeName = 'ripcord.exe'
$script:ChecksumName = 'ripcord.exe.sha256'
$script:SignatureName = 'ripcord.exe.sig'

# The public half of the release signing key, the same PEM block compiled into the binary
# (src/Ripcord.Host.Windows/Program.cs). It is here rather than fetched for the same reason it
# is compiled in there rather than read from the configuration: what a host accepts as genuine
# must not be something an attacker can hand it.
$script:SigningKeyPem = @'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEepeb3PCHe0otFYoD2YDBS7FjGnlk
hKtNTfhtSKbZ8GLsgKneO9F4/y8FpyoBjkWNwiZ73dKqs/0FXheRtBM9NA==
-----END PUBLIC KEY-----
'@

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "  $Message"
}

function Stop-Install {
    param([Parameter(Mandatory)][string] $Message)
    throw $Message
}

<#
    The public key, as its two coordinates.

    A P-256 SubjectPublicKeyInfo is always the same 91 bytes in the same shape, so this checks
    the header it expects rather than parsing ASN.1 in general: a key on another curve has to
    fail loudly here instead of being read as a P-256 point that happens to fit.
#>
function ConvertFrom-P256PublicKeyPem {
    param([Parameter(Mandatory)][string] $Pem)

    $base64 = ($Pem -split "`n" |
        Where-Object { $_ -notmatch '-----(BEGIN|END) PUBLIC KEY-----' } |
        ForEach-Object { $_.Trim() }) -join ''

    $der = [Convert]::FromBase64String($base64)

    if ($der.Length -ne 91) {
        Stop-Install "the signing key in this script is $($der.Length) bytes, not a P-256 public key"
    }

    # SEQUENCE { SEQUENCE { OID ecPublicKey, OID prime256v1 }, BIT STRING { 00 04 X Y } }
    $expected = [byte[]] @(
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00)

    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($der[$index] -ne $expected[$index]) {
            Stop-Install 'the signing key in this script is not an uncompressed P-256 public key'
        }
    }

    if ($der[26] -ne 0x04) {
        Stop-Install 'the signing key in this script is not an uncompressed point'
    }

    [PSCustomObject] @{
        X = [byte[]] $der[27..58]
        Y = [byte[]] $der[59..90]
    }
}

<#
    An ECDSA signature as `openssl dgst -sha256 -sign` writes it: SEQUENCE { INTEGER r,
    INTEGER s }, turned into the fixed 64-byte r||s that CNG wants.

    DER integers are signed, big-endian and minimal, which is where this goes wrong quietly.
    An r whose top bit is set carries a leading 0x00 so it does not read as negative, and one
    with leading zero bytes has them dropped — so r and s arrive at 33, 32 or fewer bytes and
    both have to end up at exactly 32.
#>
function ConvertFrom-DerSignature {
    param([Parameter(Mandatory)][byte[]] $Der)

    $position = 0

    if ($Der.Length -lt 8 -or $Der[$position] -ne 0x30) {
        Stop-Install 'the signature is not a DER sequence'
    }

    $position++
    $length = $Der[$position]
    $position++

    # One-byte long form, for a sequence of 128 bytes or more. A P-256 signature never reaches
    # it; handled so a malformed file fails by name rather than by index.
    if ($length -eq 0x81) {
        $length = $Der[$position]
        $position++
    }
    elseif ($length -ge 0x80) {
        Stop-Install 'the signature uses a DER length form this script does not read'
    }

    $r = Read-DerInteger -Der $Der -Position ([ref] $position)
    $s = Read-DerInteger -Der $Der -Position ([ref] $position)

    # Cast back to a byte array: concatenating two of them gives an object array, and CNG
    # wants sixty-four bytes rather than sixty-four boxed ones.
    [byte[]] ((ConvertTo-Fixed32 -Value $r) + (ConvertTo-Fixed32 -Value $s))
}

function Read-DerInteger {
    param(
        [Parameter(Mandatory)][byte[]] $Der,
        [Parameter(Mandatory)][ref] $Position)

    $at = $Position.Value

    if ($at -ge $Der.Length -or $Der[$at] -ne 0x02) {
        Stop-Install 'the signature does not hold two DER integers'
    }

    $at++
    $length = $Der[$at]
    $at++

    if ($length -eq 0) {
        Stop-Install 'the signature holds an empty integer'
    }

    if ($length -ge 0x80 -or ($at + $length) -gt $Der.Length) {
        Stop-Install 'the signature declares an integer longer than itself'
    }

    $value = [byte[]] $Der[$at..($at + $length - 1)]
    $Position.Value = $at + $length

    $value
}

function ConvertTo-Fixed32 {
    param([Parameter(Mandatory)][byte[]] $Value)

    $trimmed = $Value

    if ($trimmed.Length -gt 32 -and $trimmed[0] -eq 0x00) {
        $trimmed = [byte[]] $trimmed[1..($trimmed.Length - 1)]
    }

    if ($trimmed.Length -gt 32) {
        Stop-Install 'the signature holds a value too large for P-256'
    }

    $padded = New-Object byte[] 32
    [Array]::Copy($trimmed, 0, $padded, 32 - $trimmed.Length, $trimmed.Length)

    $padded
}

<#
    Whether these bytes were signed by the key this script carries.

    PowerShell 7 has ImportFromPem and a DER-aware VerifyData. Windows PowerShell 5.1 — which
    is what Windows Server has, and what this will actually be typed into — runs on .NET
    Framework 4.8, where neither exists: the key goes in as a CNG blob and the signature as
    r||s. Same question, two ways of asking it.
#>
function Test-ReleaseSignature {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]] $Payload,
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]] $Signature,
        [string] $PublicKeyPem = $script:SigningKeyPem)

    if ($Payload.Length -eq 0) {
        Stop-Install 'the release is empty'
    }

    if ($Signature.Length -eq 0) {
        Stop-Install 'the release carries no signature; an absent proof is a failed proof'
    }

    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $key = [System.Security.Cryptography.ECDsa]::Create()

        try {
            $key.ImportFromPem($PublicKeyPem)

            return $key.VerifyData(
                $Payload,
                $Signature,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        }
        finally {
            $key.Dispose()
        }
    }

    $point = ConvertFrom-P256PublicKeyPem -Pem $PublicKeyPem

    # BCRYPT_ECCPUBLIC_BLOB: the magic for a P-256 public key, the coordinate length, then X
    # and Y big-endian — which is how they arrive out of the SPKI, so nothing is reversed.
    $magic = [BitConverter]::GetBytes([uint32] 0x31534345)
    $size = [BitConverter]::GetBytes([uint32] 32)
    $blob = $magic + $size + $point.X + $point.Y

    $cngKey = [System.Security.Cryptography.CngKey]::Import(
        $blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)

    $key = New-Object System.Security.Cryptography.ECDsaCng($cngKey)

    try {
        $raw = ConvertFrom-DerSignature -Der $Signature
        $sha256 = [System.Security.Cryptography.SHA256]::Create()

        try {
            # The digest is computed here rather than left to VerifyData, so which hash is
            # being checked is written down rather than defaulted to.
            return $key.VerifyHash($sha256.ComputeHash($Payload), $raw)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $key.Dispose()
        $cngKey.Dispose()
    }
}

<#
    The checksum file as the release publishes it: "<hex>  ripcord.exe".
#>
function Get-PublishedChecksum {
    param([Parameter(Mandatory)][string] $ChecksumPath)

    $line = (Get-Content -LiteralPath $ChecksumPath -TotalCount 1)

    if (-not $line) {
        Stop-Install 'the checksum file is empty'
    }

    $hash = ($line -split '\s+')[0].ToLowerInvariant()

    if ($hash -notmatch '^[0-9a-f]{64}$') {
        Stop-Install 'the checksum file does not hold a SHA-256'
    }

    $hash
}

<#
    Both checks, in the order that matters: the cheap one that catches a truncated download,
    then the one that says who made it. Neither is skippable, and a release that fails either
    is left where it is rather than installed.
#>
function Assert-Release {
    param(
        [Parameter(Mandatory)][string] $Directory,

        # Overridden only by the tests, which sign with a throwaway key so the refusals can be
        # exercised without the real signing key ever leaving the release workflow.
        [string] $PublicKeyPem = $script:SigningKeyPem)

    $exe = Join-Path $Directory $script:ExeName
    $checksum = Join-Path $Directory $script:ChecksumName
    $signature = Join-Path $Directory $script:SignatureName

    foreach ($file in @($exe, $checksum, $signature)) {
        if (-not (Test-Path -LiteralPath $file)) {
            Stop-Install "$file is missing; a release is the binary, its checksum and its signature"
        }
    }

    $published = Get-PublishedChecksum -ChecksumPath $checksum
    $actual = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actual -ne $published) {
        Stop-Install "the checksum does not match: expected $published, this file is $actual"
    }

    Write-Step "checksum $actual"

    $genuine = Test-ReleaseSignature `
        -Payload ([IO.File]::ReadAllBytes($exe)) `
        -Signature ([IO.File]::ReadAllBytes($signature)) `
        -PublicKeyPem $PublicKeyPem

    if (-not $genuine) {
        Stop-Install 'the signature does not match this release and the key this script carries'
    }

    Write-Step 'signature verified against the key this script carries'
}

function Get-ReleaseUri {
    param([Parameter(Mandatory)][string] $Asset)

    if ($Version) {
        return "https://github.com/repositories/$script:RepositoryId/releases/download/$Version/$Asset"
    }

    "https://github.com/repositories/$script:RepositoryId/releases/latest/download/$Asset"
}

function Save-Release {
    param([Parameter(Mandatory)][string] $Directory)

    # Windows Server 2016 still defaults to a protocol GitHub stopped accepting years ago, and
    # the failure reads as a network error rather than as a protocol one.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    New-Item -ItemType Directory -Force -Path $Directory | Out-Null

    foreach ($asset in @($script:ExeName, $script:ChecksumName, $script:SignatureName)) {
        $uri = Get-ReleaseUri -Asset $asset
        Write-Step "fetching $asset"

        try {
            Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile (Join-Path $Directory $asset)
        }
        catch {
            Stop-Install "could not fetch $asset from $uri : $($_.Exception.Message)"
        }
    }
}

<#
    The sample configuration, which is in the repository rather than in the release: it is text
    the operator has to edit anyway, and it is fetched from the same place as this script.
    Failing to get it does not fail the install — the binary is what was being installed.
#>
function Save-SampleConfiguration {
    param(
        [Parameter(Mandatory)][string] $ForRole,
        [Parameter(Mandatory)][string] $Destination)

    $uri = "https://raw.githubusercontent.com/Hraph/Ripcord/main/config/ripcord.$ForRole.yaml"

    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $Destination
        Write-Step "sample configuration for the $ForRole host"
        return $true
    }
    catch {
        Write-Step "could not fetch the $ForRole sample configuration; copy config/ripcord.$ForRole.yaml by hand"
        return $false
    }
}

<#
    This script, into the folder it just filled.

    The host that will install from that folder is the one with no outbound access — it cannot
    fetch this file any more than it could fetch the release. So the folder has to carry the
    installer as well as what it installs.
#>
function Save-Installer {
    param([Parameter(Mandatory)][string] $Directory)

    $uri = 'https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1'

    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile (Join-Path $Directory 'install.ps1')
        Write-Step 'install.ps1, for the host that cannot fetch it'
    }
    catch {
        Write-Step 'could not fetch install.ps1; copy this script into the folder by hand'
    }
}

function Add-ToMachinePath {
    param([Parameter(Mandatory)][string] $Directory)

    $current = [Environment]::GetEnvironmentVariable('Path', 'Machine')

    if (($current -split ';') -contains $Directory) {
        return
    }

    [Environment]::SetEnvironmentVariable('Path', "$current;$Directory", 'Machine')
    Write-Step "added $Directory to the machine PATH"
}

<#
    The scheduled task of milestone 5. Refused when the configuration has no alerting block:
    the task would otherwise print "alerting is switched off" every fifteen minutes for ever,
    which is how a scheduled task becomes something nobody reads.
#>
function Add-CheckTask {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string] $ConfigurationPath)

    if (-not (Test-Path -LiteralPath $ConfigurationPath)) {
        Write-Step 'no ripcord.yaml yet, so no scheduled task: create it once the file is filled in'
        return
    }

    if (-not (Select-String -LiteralPath $ConfigurationPath -Pattern '^\s*alerting:' -Quiet)) {
        Write-Step 'the configuration has no alerting block, so no scheduled task was created'
        return
    }

    $command = "`"$Executable`" check --notify"

    & schtasks.exe /Create /TN 'Ripcord check' /SC MINUTE /MO 15 /RL HIGHEST /RU SYSTEM `
        /TR $command /F | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Stop-Install "schtasks refused to create the task (exit $LASTEXITCODE)"
    }

    Write-Step 'scheduled task "Ripcord check" every fifteen minutes'
}

function Add-DashboardShortcut {
    param([Parameter(Mandatory)][string] $ConfigurationPath)

    if (-not (Test-Path -LiteralPath $ConfigurationPath)) {
        Write-Step 'no ripcord.yaml yet, so no dashboard shortcut'
        return
    }

    $dashboard = Select-String -LiteralPath $ConfigurationPath -Pattern '^\s*dashboard:' -Quiet

    if (-not $dashboard) {
        Write-Step 'the configuration has no dashboard block, so no shortcut was created'
        return
    }

    $port = 7080
    $configured = Select-String -LiteralPath $ConfigurationPath -Pattern '^\s+port:\s*(\d+)' |
        Select-Object -Last 1

    if ($configured) {
        $port = [int] $configured.Matches[0].Groups[1].Value
    }

    $startMenu = [Environment]::GetFolderPath('CommonPrograms')
    $shortcut = Join-Path $startMenu 'Ripcord dashboard.url'

    # A .url file rather than a .lnk: it is three lines of text, and it needs no COM object to
    # write on a host where scripting objects may be locked down.
    Set-Content -LiteralPath $shortcut -Encoding ASCII -Value @(
        '[InternetShortcut]'
        "URL=http://127.0.0.1:$port"
    )

    Write-Step "Start Menu shortcut to http://127.0.0.1:$port"
}

function Assert-Windows {
    # $IsWindows exists on PowerShell 6 and later; on 5.1 there is no other platform to be on.
    if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
        Stop-Install 'Ripcord runs on Windows: this installs a win-x64 binary and a Windows service'
    }
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Stop-Install 'run this from an elevated PowerShell: it writes to Program Files, the machine PATH and the task scheduler'
    }
}

function Invoke-Install {
    # Set here rather than at the top of the file: dot-sourcing this script to reach its
    # functions must not impose strict mode on whoever did the sourcing.
    Set-StrictMode -Version 2.0

    Assert-Windows

    Write-Host ''
    Write-Host 'ripcord - disaster recovery for a Hyper-V Replica pair'
    Write-Host ''

    # Download and verify, install nothing. The other half of an offline install.
    if ($Prepare) {
        Save-Release -Directory $Prepare
        Assert-Release -Directory $Prepare
        Save-Installer -Directory $Prepare

        foreach ($role in @('primary', 'dr')) {
            Save-SampleConfiguration -ForRole $role `
                -Destination (Join-Path $Prepare "ripcord.$role.yaml") | Out-Null
        }

        Write-Host ''
        Write-Host "  Verified, and in $Prepare."
        Write-Host '  Copy that folder to each host. There, from an elevated PowerShell:'
        Write-Host ''
        Write-Host '      powershell -ExecutionPolicy Bypass -File .\install.ps1 -FromPath .'
        Write-Host ''
        return
    }

    Assert-Elevated

    $exe = Join-Path $Path $script:ExeName
    $configuration = Join-Path $Path 'ripcord.yaml'

    if ((Test-Path -LiteralPath $exe) -and -not $Force) {
        Stop-Install "$exe is already here. Updating an installed host is 'ripcord update', which checks the signature and keeps the binary it replaces. Use -Force only to reinstall."
    }

    $source = $FromPath

    if (-not $source) {
        $source = Join-Path ([IO.Path]::GetTempPath()) ("ripcord-" + [Guid]::NewGuid().ToString('N'))
        Save-Release -Directory $source
    }

    Assert-Release -Directory $source

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $script:ExeName) -Destination $exe -Force
    Write-Step "installed $exe"

    # An existing configuration is never touched, in any mode: it is the one file on the host
    # this script could destroy something with.
    if (Test-Path -LiteralPath $configuration) {
        Write-Step 'kept the ripcord.yaml already here'
    }
    else {
        $chosen = $Role

        if (-not $chosen) {
            $answer = Read-Host 'Which host is this - the primary or the DR host? [primary/dr]'
            $chosen = if ($answer -match '^(d|dr)$') { 'dr' } else { 'primary' }
        }

        $fromFolder = Join-Path $source "ripcord.$chosen.yaml"

        if (Test-Path -LiteralPath $fromFolder) {
            Copy-Item -LiteralPath $fromFolder -Destination $configuration
            Write-Step "sample configuration for the $chosen host"
        }
        else {
            Save-SampleConfiguration -ForRole $chosen -Destination $configuration | Out-Null
        }
    }

    Add-ToMachinePath -Directory $Path

    if ($CheckTask) {
        Add-CheckTask -Executable $exe -ConfigurationPath $configuration
    }

    if ($Shortcut) {
        Add-DashboardShortcut -ConfigurationPath $configuration
    }

    if (-not $FromPath) {
        Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Installed is not working, and an installer that blurs the two sends somebody away
    # believing a pair is protected. The node name, the peer address and both certificate
    # thumbprints are per-host, and `ripcord status` refuses a file that names another machine.
    Write-Host ''
    Write-Host '  Installed. It is not configured yet:'
    Write-Host ''
    Write-Host "      1. edit $configuration - node, peer and the two thumbprints"
    Write-Host '      2. ripcord status                  (refuses until the file is right)'
    Write-Host '      3. ripcord check                   (would a failover work right now)'
    Write-Host '      4. ripcord deploy-listener --dry-run'
    Write-Host ''
    Write-Host '  Then do the same on the other host.'
    Write-Host ''
}

if (-not $SourceOnly) {
    Invoke-Install
}
