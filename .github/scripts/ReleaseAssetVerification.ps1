Set-StrictMode -Version Latest

function Test-ExistingReleaseAsset {
    param(
        [AllowEmptyCollection()][object[]] $Assets,
        [Parameter(Mandatory)][string] $ExpectedName,
        [Parameter(Mandatory)][long] $ExpectedSize,
        [Parameter(Mandatory)][string] $ExpectedSha256
    )
    if ($ExpectedName -cnotin @('DS4Updater.exe', 'DS4Updater_x86.exe') -or
        $ExpectedSize -le 0 -or $ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Invalid locally built updater asset identity.'
    }
    $matchingAssets = @($Assets | Where-Object {
        [string]::Equals($_.name, $ExpectedName, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matchingAssets.Count -eq 0) { return $false }
    if ($matchingAssets.Count -ne 1 -or $matchingAssets[0].name -cne $ExpectedName -or
        $matchingAssets[0].state -cne 'uploaded' -or $matchingAssets[0].size -ne $ExpectedSize -or
        $matchingAssets[0].digest -ine "sha256:$ExpectedSha256") {
        throw 'Existing release asset is ambiguous or differs from the verified build; it will not be overwritten.'
    }
    return $true
}
