<#
    The installer's verification, under whichever PowerShell is running these.

    The point of running the same file under 5.1 and 7 is that they verify by different means —
    5.1 has neither ImportFromPem nor a DER-aware VerifyData — and the host these will actually
    be typed into is the one with 5.1 on it.

    The fixtures come from New-SignatureVectors.ps1, which signs with a throwaway key. The real
    signing key never leaves the release workflow's secret, and nothing here needs it.
#>

BeforeAll {
    $script:RepositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

    . (Join-Path $script:RepositoryRoot 'install.ps1') -SourceOnly

    $script:Vectors = if ($env:RIPCORD_SIGNATURE_VECTORS) {
        $env:RIPCORD_SIGNATURE_VECTORS
    }
    else {
        Join-Path $PSScriptRoot 'vectors'
    }

    $script:PublicKey = Get-Content -LiteralPath (Join-Path $script:Vectors 'key.pub.pem') -Raw
    $script:StrangerKey = Get-Content -LiteralPath (Join-Path $script:Vectors 'stranger.pub.pem') -Raw
    $script:Payload = [IO.File]::ReadAllBytes((Join-Path $script:Vectors 'payload.bin'))
    $script:Tampered = [IO.File]::ReadAllBytes((Join-Path $script:Vectors 'tampered.bin'))
    $script:Good = [IO.File]::ReadAllBytes((Join-Path $script:Vectors 'good.sig'))
    $script:Stranger = [IO.File]::ReadAllBytes((Join-Path $script:Vectors 'stranger.sig'))
    $script:PaddedR = [IO.File]::ReadAllBytes((Join-Path $script:Vectors 'padded-r.sig'))
}

Describe 'the release signature' {
    It 'accepts what the pinned key signed' {
        Test-ReleaseSignature -Payload $script:Payload -Signature $script:Good `
            -PublicKeyPem $script:PublicKey | Should -BeTrue
    }

    It 'refuses a payload with one byte changed' {
        Test-ReleaseSignature -Payload $script:Tampered -Signature $script:Good `
            -PublicKeyPem $script:PublicKey | Should -BeFalse
    }

    It 'refuses a signature made by somebody else' {
        Test-ReleaseSignature -Payload $script:Payload -Signature $script:Stranger `
            -PublicKeyPem $script:PublicKey | Should -BeFalse
    }

    It 'refuses the right signature against the wrong key' {
        Test-ReleaseSignature -Payload $script:Payload -Signature $script:Good `
            -PublicKeyPem $script:StrangerKey | Should -BeFalse
    }

    <#
        The case the fixed-width conversion exists for: a DER integer whose top bit is set
        carries a leading zero byte, so r arrives at 33 bytes and has to become 32 without
        losing a byte of the value.
    #>
    It 'accepts a signature whose r carries a DER sign byte' {
        Test-ReleaseSignature -Payload $script:Payload -Signature $script:PaddedR `
            -PublicKeyPem $script:PublicKey | Should -BeTrue
    }

    It 'accepts a signature whose r lost its leading zeros to minimal DER' {
        $short = Join-Path $script:Vectors 'short-r.sig'

        if (-not (Test-Path -LiteralPath $short)) {
            Set-ItResult -Skipped -Because 'no short-r signature appeared in the fixture run'
            return
        }

        Test-ReleaseSignature -Payload $script:Payload -Signature ([IO.File]::ReadAllBytes($short)) `
            -PublicKeyPem $script:PublicKey | Should -BeTrue
    }

    It 'refuses an absent signature rather than reading it as a pass' {
        { Test-ReleaseSignature -Payload $script:Payload -Signature ([byte[]] @()) `
                -PublicKeyPem $script:PublicKey } |
            Should -Throw '*absent proof*'
    }

    It 'refuses an empty release' {
        { Test-ReleaseSignature -Payload ([byte[]] @()) -Signature $script:Good `
                -PublicKeyPem $script:PublicKey } |
            Should -Throw '*empty*'
    }

    <#
        The two hosts refuse this differently and both are right. PowerShell 7 hands the DER
        to .NET, which reads a malformed sequence as "does not verify" and returns false;
        5.1 reads the DER here and stops by name. Asserting either shape passes on one host
        and fails on the other, so what is asserted is the property that matters: whatever it
        does, it must not come back true. The named refusal is covered directly against
        `ConvertFrom-DerSignature`, below.
    #>
    It 'refuses a signature that is not a DER sequence, however it refuses it' {
        $accepted = $true

        try {
            $accepted = Test-ReleaseSignature -Payload $script:Payload `
                -Signature ([byte[]] (1..40)) -PublicKeyPem $script:PublicKey
        }
        catch {
            $accepted = $false
        }

        $accepted | Should -BeFalse
    }
}

Describe 'the key this script carries' {
    It 'is the one compiled into the binary' {
        $inProgram = Get-Content -LiteralPath (
            Join-Path $script:RepositoryRoot 'src/Ripcord.Host.Windows/Program.cs') -Raw

        $body = ($script:SigningKeyPem -split "`n" |
            Where-Object { $_ -notmatch '-----' } |
            ForEach-Object { $_.Trim() }) -join ''

        # Two copies of one fact. They drift the day the key is rotated and only one is
        # changed, and the host would then refuse the release it was handed.
        $inProgram.Replace("`r", '').Replace("`n", '').Replace(' ', '') |
            Should -BeLike "*$body*"
    }

    It 'reads as a P-256 point' {
        $point = ConvertFrom-P256PublicKeyPem -Pem $script:SigningKeyPem

        $point.X.Length | Should -Be 32
        $point.Y.Length | Should -Be 32
    }

    It 'refuses a key that is not P-256' {
        { ConvertFrom-P256PublicKeyPem -Pem $script:StrangerKey.Replace('MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE', 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQgDQgAE') } |
            Should -Throw
    }
}

Describe 'the DER signature reader' {
    It 'always produces sixty-four bytes' {
        (ConvertFrom-DerSignature -Der $script:Good).Length | Should -Be 64
        (ConvertFrom-DerSignature -Der $script:PaddedR).Length | Should -Be 64
    }

    It 'pads a short value on the left, where the zeros belong' {
        $padded = ConvertTo-Fixed32 -Value ([byte[]] @(0x01, 0x02))

        $padded.Length | Should -Be 32
        $padded[30] | Should -Be 1
        $padded[31] | Should -Be 2
        $padded[0] | Should -Be 0
    }

    It 'drops the DER sign byte rather than the value' {
        $value = [byte[]] (@(0x00, 0xFF) + (1..31))
        $fixed = ConvertTo-Fixed32 -Value $value

        $fixed.Length | Should -Be 32
        $fixed[0] | Should -Be 0xFF
    }

    It 'refuses a value too large for the curve' {
        { ConvertTo-Fixed32 -Value ([byte[]] (1..33)) } | Should -Throw
    }

    It 'says what is wrong when the bytes are not a sequence at all' {
        { ConvertFrom-DerSignature -Der ([byte[]] (1..40)) } | Should -Throw '*DER sequence*'
    }

    It 'refuses a length form it does not read rather than guessing at one' {
        # 0x30 then a two-byte long form, which a P-256 signature never uses.
        { ConvertFrom-DerSignature -Der ([byte[]] (@(0x30, 0x82) + (1..40))) } |
            Should -Throw '*length form*'
    }
}

Describe 'where a release is asked for' {
    <#
        The route that was wrong, pinned so it cannot come back. `github.com/repositories/{id}`
        is not a path GitHub serves — it answers 404 for everything under it, so an installer
        built on it could never download anything. The numeric id belongs to the API.
    #>
    It 'asks the API, by the repository id' {
        Get-ReleaseApiUri |
            Should -Be 'https://api.github.com/repositories/1367653231/releases/latest'
    }

    It 'asks for a named tag when one was given' {
        Get-ReleaseApiUri -Tag 'v0.1.0' |
            Should -Be 'https://api.github.com/repositories/1367653231/releases/tags/v0.1.0'
    }

    It 'fetches a file by its own id, not by an address the answer offered' {
        Get-ReleaseAssetUri -AssetId 542974244 |
            Should -Be 'https://api.github.com/repositories/1367653231/releases/assets/542974244'
    }

    <#
        Every address this script builds is two numbers and a host. The repository is never
        named, so it cannot be renamed out from under the installer and the old name cannot be
        recreated by somebody else — which is the whole reason for the id.
    #>
    It 'names no repository in any address it builds' {
        $addresses = @(
            (Get-ReleaseApiUri),
            (Get-ReleaseApiUri -Tag 'v1.2.3'),
            (Get-ReleaseAssetUri -AssetId 1))

        foreach ($address in $addresses) {
            $address | Should -Not -BeLike 'https://github.com/repositories/*'
            $address | Should -Not -Match 'ripcord'
            $address | Should -BeLike 'https://api.github.com/repositories/1367653231/*'
        }
    }

    It 'carries the repository id the binary carries' {
        $program = Get-Content -LiteralPath (
            Join-Path $script:RepositoryRoot 'src/Ripcord.Host.Windows/Program.cs') -Raw

        # Written 1_367_653_231 there and 1367653231 here; the same number either way, and the
        # day one moves without the other the installer would fetch a stranger's releases.
        $declared = [regex]::Match($program, 'RepositoryId\s*=\s*([0-9_]+)')

        $declared.Success | Should -BeTrue
        $declared.Groups[1].Value.Replace('_', '') | Should -Be "$script:RepositoryId"
    }
}

Describe 'the published checksum' {
    BeforeAll {
        $script:Directory = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:Directory | Out-Null
    }

    AfterAll {
        Remove-Item -LiteralPath $script:Directory -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'is read as the release publishes it' {
        $file = Join-Path $script:Directory 'ok.sha256'
        $hash = 'a' * 64
        Set-Content -LiteralPath $file -Value "$hash  ripcord.exe"

        Get-PublishedChecksum -ChecksumPath $file | Should -Be $hash
    }

    It 'refuses anything that is not one' {
        $file = Join-Path $script:Directory 'bad.sha256'
        Set-Content -LiteralPath $file -Value 'not a hash  ripcord.exe'

        { Get-PublishedChecksum -ChecksumPath $file } | Should -Throw '*SHA-256*'
    }
}

Describe 'a release as a whole' {
    BeforeEach {
        $script:Release = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $script:Release | Out-Null

        [IO.File]::WriteAllBytes((Join-Path $script:Release 'ripcord.exe'), $script:Payload)
        [IO.File]::WriteAllBytes((Join-Path $script:Release 'ripcord.exe.sig'), $script:Good)

        $hash = (Get-FileHash -LiteralPath (Join-Path $script:Release 'ripcord.exe') `
                -Algorithm SHA256).Hash.ToLowerInvariant()

        Set-Content -LiteralPath (Join-Path $script:Release 'ripcord.exe.sha256') `
            -Value "$hash  ripcord.exe"
    }

    AfterEach {
        Remove-Item -LiteralPath $script:Release -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'refuses a folder with no signature in it' {
        Remove-Item -LiteralPath (Join-Path $script:Release 'ripcord.exe.sig')

        { Assert-Release -Directory $script:Release -PublicKeyPem $script:PublicKey } | Should -Throw '*signature*'
    }

    It 'refuses a binary the checksum does not describe' {
        [IO.File]::WriteAllBytes((Join-Path $script:Release 'ripcord.exe'), $script:Tampered)

        { Assert-Release -Directory $script:Release -PublicKeyPem $script:PublicKey } | Should -Throw '*checksum does not match*'
    }

    <#
        Both files consistent with each other and with nothing else: the checksum was recomputed
        over the tampered binary, so only the signature can still tell. This is the case a
        checksum alone cannot catch.
    #>
    It 'refuses a binary that was replaced together with its checksum' {
        [IO.File]::WriteAllBytes((Join-Path $script:Release 'ripcord.exe'), $script:Tampered)

        $hash = (Get-FileHash -LiteralPath (Join-Path $script:Release 'ripcord.exe') `
                -Algorithm SHA256).Hash.ToLowerInvariant()

        Set-Content -LiteralPath (Join-Path $script:Release 'ripcord.exe.sha256') `
            -Value "$hash  ripcord.exe"

        { Assert-Release -Directory $script:Release -PublicKeyPem $script:PublicKey } | Should -Throw '*signature does not match*'
    }
}
