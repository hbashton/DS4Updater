$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ReleaseAssetVerification.ps1"
$hash = 'a' * 64
$valid = @{ name = 'DS4Updater.exe'; size = 12345L; digest = "sha256:$hash"; state = 'uploaded' }
$checks = 0
function Assert-Rejected([scriptblock] $Action) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (!$threw) { throw 'Expected the asset identity check to reject the fixture.' }
}
if (Test-ExistingReleaseAsset -Assets @() -ExpectedName 'DS4Updater.exe' -ExpectedSize 12345 -ExpectedSha256 $hash) {
    throw 'An absent asset must still need upload.'
}
$checks++
foreach ($name in @('DS4Updater.exe', 'DS4Updater_x86.exe')) {
    $asset = $valid.Clone(); $asset.name = $name
    if (!(Test-ExistingReleaseAsset -Assets @([pscustomobject]$asset) -ExpectedName $name -ExpectedSize 12345 -ExpectedSha256 $hash)) {
        throw 'An exact existing build must be accepted without upload.'
    }
    $checks++
}
foreach ($mutation in @(
    @{ name = 'ds4updater.exe' }, @{ size = 12344L }, @{ digest = $null },
    @{ digest = ('sha256:' + ('b' * 64)) }, @{ state = 'new' }
)) {
    $asset = $valid.Clone()
    foreach ($key in $mutation.Keys) { $asset[$key] = $mutation[$key] }
    Assert-Rejected { Test-ExistingReleaseAsset -Assets @([pscustomobject]$asset) -ExpectedName 'DS4Updater.exe' -ExpectedSize 12345 -ExpectedSha256 $hash }
    $checks++
}
Assert-Rejected { Test-ExistingReleaseAsset -Assets @([pscustomobject]$valid, [pscustomobject]$valid) -ExpectedName 'DS4Updater.exe' -ExpectedSize 12345 -ExpectedSha256 $hash }
$checks++
Assert-Rejected { Test-ExistingReleaseAsset -Assets @() -ExpectedName 'other.exe' -ExpectedSize 12345 -ExpectedSha256 $hash }
$checks++
Assert-Rejected { Test-ExistingReleaseAsset -Assets @() -ExpectedName 'DS4Updater.exe' -ExpectedSize 0 -ExpectedSha256 $hash }
$checks++
Assert-Rejected { Test-ExistingReleaseAsset -Assets @() -ExpectedName 'DS4Updater.exe' -ExpectedSize 12345 -ExpectedSha256 'bad' }
$checks++
$workflow = Get-Content -LiteralPath "$PSScriptRoot/../workflows/release.yml" -Raw
foreach ($required in @('Test-ExistingReleaseAsset', 'git rev-parse HEAD', 'git rev-parse "$($env:RELEASE_TAG)^{commit}"', '$env:GITHUB_SHA')) {
    if (!$workflow.Contains($required)) { throw "Workflow lost required release identity guard: $required" }
    $checks++
}
if ($workflow.Contains('--clobber')) { throw 'Immutable release assets must never be overwritten.' }
$checks++
Write-Output "$checks release-asset identity checks passed; no network or release mutation was performed."
