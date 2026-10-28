# MemPilot local code-signing
#   tools/sign.ps1 -Create           one-time: create self-signed code-signing cert,
#                                    export public part to tools/, trust it on this machine
#   tools/sign.ps1 [-TargetPath x]   sign the exe(s) (default: all exes in the Release output)
param(
  [switch]$Create,
  [string]$TargetPath = ""
)
$ErrorActionPreference = 'Stop'
$certSubject = 'CN=MemPilot Dev Signing'
$pubPath = Join-Path $PSScriptRoot 'MemPilotDev.cer'

if ($Create) {
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject `
      -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(10) `
      -KeyUsage DigitalSignature -KeyUsageProperty Sign
  Export-Certificate -Cert $cert -FilePath $pubPath | Out-Null
  # trust locally so SmartScreen / UAC stop flagging our own builds (needs elevation)
  Import-Certificate -FilePath $pubPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
  Import-Certificate -FilePath $pubPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
  Write-Output "created+trusted: $($cert.Thumbprint) ($pubPath)"
  exit 0
}

# accept either the new or the legacy cert subject (cert created before the rename)
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -in @('CN=MemPilot Dev Signing', 'CN=MemoryTool Dev Signing') } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { Write-Output 'NO_CERT - run tools/sign.ps1 -Create once (elevated)'; exit 1 }

if (-not $TargetPath) {
  $paths = Get-ChildItem (Join-Path $PSScriptRoot '..\src\MemoryTool.App\bin\Release\net10.0-windows') -Filter *.exe -ErrorAction SilentlyContinue
} else {
  $paths = Get-Item $TargetPath -ErrorAction SilentlyContinue
}
foreach ($t in $paths) {
  $sig = Set-AuthenticodeSignature -FilePath $t.FullName -Certificate $cert -IncludeChain Signer
  Write-Output ("signed: " + $t.Name + " -> " + $sig.Status)
}
