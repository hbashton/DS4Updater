# DS4Updater 2.0.6

## Changes

- Named DS4Windows releases no longer need a new updater version for each release.
  The updater obtains the expected Windows binary version from the release's
  `RELEASE-BUILD.json`, verified against GitHub's asset SHA-256 and size.
- The build record must identify the exact repository, tag and release ID, and
  bind the selected portable ZIP to the same SHA-256 as GitHub's metadata. A
  record cannot override an established historical or numeric version identity.
- Release-channel ordering and Windows binary downgrade protection remain
  independent requirements. Staged, installed and manually reopened applications
  must match the verified release identity. A target changed during preparation
  is rejected before the portable transaction starts.
- Existing historical release mappings remain available only when a build record
  is absent. An invalid, ambiguous or tampered record never triggers fallback.
- The portable protocol, ownership manifest, profile preservation and broker
  requirements are unchanged. No new VIIPER release is required.

The safe portable path remains x64. Both x64 and x86 updater executables are
built by the existing release workflow; x86 does not gain a new portable-update
protocol. Existing DS4Windows clients using the verified updater bootstrap can
obtain this release through the normal stable updater channel.

Build records are publisher metadata authenticated by the GitHub asset digest;
this is not a claim that the JSON record or executable has a digital signature.

## Local validation and publication boundary

The final full suite passed **313 / 313**, zero failures or skips, including the
immutable real-release ZIP fixture. Report:
`isolated_results/updater-2.0.6/final-tests/updater-2.0.6-final-full.trx`.
The regression matrix includes future RC4.5.4/RC4.6 records, malformed and
tampered records, legacy fallback, changed-target rejection, and the numeric
workflow's deterministic leading-`v` package-marker/version normalization.
Named release records still require four explicit Windows version components.

Validation uses synthetic HTTP/process fixtures and isolated filesystem targets,
including the unchanged published RC4.5.1 ZIP. It does not execute an installed
DS4Windows, updater worker or broker, and does not change a physical controller
session. A launched-worker end-to-end acceptance remains separate from these
tests and the verified real-ZIP filesystem transaction.

Run from the updater repository:

```powershell
$env:DS4UPDATER_RC451_PACKAGE = 'C:\Users\hbash\Desktop\DS4Windows-RC4.5.1-Publish-2026-09-09\release-assets\DS4Windows_VIIPER_x64.zip'
dotnet test .\Updater2.Tests\DS4Updater.Tests.csproj -c Release --logger 'trx;LogFileName=updater-2.0.6-full.trx' --results-directory .\isolated_results\updater-2.0.6\tests
dotnet publish .\Updater2\DS4Updater.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true -o .\isolated_results\updater-2.0.6\publish-x64
```

The immutable test ZIP has SHA-256
`985B7DA39AB682FAE9ED27EC4B1622F58C240911454CFFC29CD661EC13BBA109`.
The same publish flags with `win-x86` and `Platform=x86` cover the second
workflow asset. Local build outputs are not publication evidence.

The repository's primary branch is `master`; this work descends from
`d2e9b3ae316c52b8320055e2acc063da42d05905` (`v2.0.5`). The existing workflow tests
the immutable ZIP and publishes one compressed, self-contained executable per
architecture. It uploads release assets only for a published release event, not
for `workflow_dispatch`, and never replaces an existing asset with `--clobber`.
Release coordination must verify both assets before promoting 2.0.6 to stable
latest; 2.0.5 remains immutable.
