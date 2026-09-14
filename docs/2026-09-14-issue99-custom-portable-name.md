# DS4Windows #99 — custom portable executable updates

Source fix for [DS4Windows issue #99](https://github.com/hbashton/DS4Windows/issues/99).
This is **unreleased source**, not a replacement for published updater 2.0.6.

The old transaction added the configured alias while also reinstalling the
default `DS4Windows.exe`. The corrected transaction installs the apphost under
the selected name and records that transformed ownership, without changing the
downloaded archive or its manifest. Canonical `DS4Windows.dll`, runtimeconfig and
dependency files remain because the apphost still targets that managed assembly.
Alias sidecars are refreshed as well.

Owned default executables and obsolete owned aliases use the normal backup,
delete, verification and rollback path. Unowned collisions reject before live
mutation; unowned historical files are preserved. The setting file is never
rewritten and an existing setting is held read-only through apply/rollback.
An initially missing setting must remain missing throughout the transaction.

The external `--portable-safe-v1 --launchExe` contract remains compatible with
existing DS4Windows. The launcher resolves the saved name and binds both the
initiating image and selected destination into the immutable worker request.
`--originalExe` is internal-worker-only. Pre-update identity checks use the old
image; staged checks use the canonical package; final verification and Open use
the selected destination. Name changes and clearing a name therefore work even
when the destination does not exist yet. Exact-root process checks watch old,
new and default images without stopping any process; a live initiating PID and
start time must match the original exact executable, not just any adjacent image.

## Verification

- Final x64 Release suite: **358 passed, zero failed, one explicit skip** out of
  359 tests. The skip is the historical RC4.5.1 ZIP fixture, whose archive is not
  present; it is not a skipped unit regression.
- Actual published RC4.6.1 ZIP fixture enabled with `DS4UPDATER_RC461_PACKAGE`:
  `137650321` bytes, SHA-256
  `0B5B05E491AA01F6EAC58A33B1742FEA62481F7ABDBBC5EBAAE64BE7E7604541`.
  Two complete custom-name transactions verify all 553 archive entries,
  transformed ownership, alias/sidecar hashes, required canonical dependencies,
  PE identity `5.0.7.0` / `VIIPERRC4.6.1`, absence of the default executable,
  removal of the previous owned alias and preservation of synthetic user data.
- Unit regressions cover missing/present default EXEs, late-failure rollback,
  consecutive updates, changed/cleared names, unsafe and unowned collisions,
  locked/concurrently created settings, case-insensitive Windows names,
  worker-record tampering, and exact original/destination process identities.
- Independent review checked the worker/coordinator/transaction contract and
  the matching DS4Windows protected-setup-staging compatibility change.

Evidence: `_results/issue99-final/issue99-final.trx`. All filesystem transactions
target generated disposable fixtures. No DS4Windows, broker, installer or updater
executable is launched, and no production folder or real user profile is changed.
This does not claim a live GUI update/reboot test or public delivery.
