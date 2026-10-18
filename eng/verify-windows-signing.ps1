# Fails, naming each file and its problem, unless a sandboxed layout's exe, <Exe>.dll and chrome_elf.dll are all
# unsigned or all signed by one trusted certificate. bootstrap.exe refuses anything else with only a stderr FATAL and
# exit code 0x80000003 (docs/DRIFT.md P13). Run it after signing.
# Usage: eng/verify-windows-signing.ps1 <path-to-Exe.exe>
param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path $Exe).Path
$dir = Split-Path $exePath
$files = @($exePath, [IO.Path]::ChangeExtension($exePath, '.dll'), (Join-Path $dir 'chrome_elf.dll'))
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "$f is missing; is $exePath a sandboxed layout?" } }

$sigs = foreach ($f in $files) {
    $s = Get-AuthenticodeSignature -LiteralPath $f
    [pscustomobject]@{ Name = [IO.Path]::GetFileName($f); Status = $s.Status; Message = $s.StatusMessage; Cert = $s.SignerCertificate }
}
$signed = @($sigs | Where-Object Status -ne 'NotSigned')
if (-not $signed) {
    "$([IO.Path]::GetFileName($exePath)): exe, DLL and chrome_elf.dll unsigned"
    return
}

$problems = [Collections.Generic.List[string]]::new()
$signedNames = "$(($signed.Name) -join ' and ') $(if ($signed.Count -gt 1) { 'are' } else { 'is' })"
foreach ($s in $sigs | Where-Object Status -eq 'NotSigned') {
    $hint = if ($s.Name -eq 'chrome_elf.dll') { '; CEF ships it unsigned, so sign it with the app''s certificate' } else { '' }
    $problems.Add("$($s.Name) is unsigned but $signedNames signed$hint")
}
# WinVerifyTrust's verdict, which bootstrap applies: a self-signed certificate from an untrusted root fails here.
foreach ($s in $signed | Where-Object Status -ne 'Valid') {
    $problems.Add("$($s.Name): signature not accepted ($($s.Status): $($s.Message))")
}
$thumbs = @($signed | Where-Object Cert | Group-Object { $_.Cert.Thumbprint })
if ($thumbs.Count -gt 1) {
    $by = ($thumbs | ForEach-Object { "$(($_.Group.Name) -join ', ') by $($_.Group[0].Cert.Subject) ($($_.Name))" }) -join '; '
    $problems.Add("signed by different certificates: $by")
}
if ($problems.Count) { throw "bootstrap.exe would refuse to start ${exePath}:`n  " + ($problems -join "`n  ") }
"$([IO.Path]::GetFileName($exePath)): exe, DLL and chrome_elf.dll signed by $($signed[0].Cert.Subject) ($($signed[0].Cert.Thumbprint))"
