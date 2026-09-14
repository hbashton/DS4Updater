# DS4Updater 2.0.7

## Changes

- Preserves the selected custom portable executable name without reinstalling a
  duplicate `DS4Windows.exe`. Required canonical managed-assembly dependencies
  remain, and aliases and ownership manifests are updated transactionally.
- Supports changed and cleared custom names by binding the initiating executable
  separately from the selected destination in the immutable worker request.
- Protects configuration bytes, rejects unowned collisions before mutation, and
  restores owned files on failure using the existing verified rollback path.
  A newly selected alias never adopts preexisting unowned files. A legacy
  custom apphost can be adopted only when it is the exact initiating executable
  bound to the worker, its PE identity matches the owned application assembly,
  and any unowned sidecars match their owned canonical counterparts. Read locks
  and exclusive preflight hash checks bind that evidence to the actual files.
- Gates custom-only layouts on the **verified target binary version 5.0.8.0 or
  newer**, first delivered by DS4Windows RC4.6.2. An older custom-target request
  fails before ZIP download, staging, process waiting, or live-file mutation.
  The existing layout is left intact. Canonical updates to older supported
  targets remain available; this is not a hardcoded release-tag allowlist.
- Retains 2.0.6's authenticated release-build-record checks, staged/final identity
  checks, exact-root process guard, and forward-release/downgrade protections.

The portable-safe-v1 bootstrap contract is unchanged. An old initiating app can
use this updater to reach RC4.6.2 or a compatible later release. A cached older
payload cannot pass the target's verified PE identity check. The supported safe
portable architecture remains x64; the x86 legacy asset is still produced.

## Release coordination

Use tag `v2.0.7`, matching the existing stable `v2.0.6` naming convention. The
workflow produces exactly two compressed self-contained assets:

- `DS4Updater.exe` — win-x64
- `DS4Updater_x86.exe` — win-x86

The workflow builds on source push and uploads immutable release assets on a
published release event. It does not clobber existing assets. Verify the
workflow's exact commit, successful tests, file version, asset byte length, and
GitHub SHA-256 digest for both assets before making this the stable `/latest`
release. The DS4Windows release does not bundle this updater; its verified
bootstrap selects the stable updater release independently.

For draft-first coordination, preload the exact successful source-CI artifacts
into the `v2.0.7` draft and verify both assets before publication. The published
workflow then rebuilds the exact tag/event commit and accepts an existing asset
only when its unique exact name, uploaded state, size and GitHub SHA-256 match
the newly built file. Missing assets may be uploaded; duplicates or mismatches
fail without overwriting anything. An empty draft must never be promoted to
stable/latest while waiting for its binaries.

Publish and verify updater 2.0.7 before DS4Windows RC4.6.2. The compatibility gate
makes the interval safe: a custom-named old application requesting an older
target is refused without file changes until a compatible target is available.
DS4Windows RC4.6.2 must require updater 2.0.7 or later for the corrected behavior.

## Validation

Unit and orchestration cases cover the exact compatibility boundary, older
initiating applications, canonical historical updates, changed/cleared names,
future verified versions, and an incompatible cached staged payload. The CI
workflow also verifies the SHA-256-pinned RC4.5.1 and RC4.6.1 archives. RC4.6.1 is
a low-level filesystem transaction regression; production custom updates to
that target are refused by the coordinator.

Before declaring RC4.6.2 integration complete, run the actual candidate/archive
transaction fixture with `DS4UPDATER_RC462_PACKAGE` and its independently verified
release SHA-256 in `DS4UPDATER_RC462_SHA256`. It verifies every payload file,
two consecutive selected names, sidecars, ownership, canonical dependency
retention, exact `5.0.8.0` / `VIIPERRC4.6.2` PE identity, and synthetic user-data
preservation. It does not execute an app, worker, broker, or installer.

Test reports and local publish outputs are retained under `_results/updater-2.0.7`.
Local builds are validation evidence, not evidence of public asset delivery.

Local pre-publication validation: **386 passed, zero failed, one explicit skip**
(the not-yet-built RC4.6.2 archive fixture), out of 387 tests. Both immutable
historical archive fixtures ran. The two architecture publishes each produced
one self-contained executable with version `2.0.7`. Seventeen pure PowerShell
release-identity cases passed, including no-overwrite/duplicate/mismatched-digest
rejection and the release workflow's exact source/tag guard.
