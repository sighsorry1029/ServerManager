# FreshWorld and Upgrade World maintenance

ServerManager automatically coordinates its tracked Upgrade World cron jobs with
a compatible FreshWorld. Keep the existing `cron.yml` schedule: there is no new
setting or prerequisite mapping. FreshWorld keeps its own schedule.

## What it does

1. Wait while FreshWorld is running or finishing cleanup.
2. Atomically acquire FreshWorld's shared maintenance lock before dispatch.
3. Run the Upgrade World job and observe its actual operations.
4. Release the lock only after the tracked work has stopped and any owned
   pending request has been drained.

FreshWorld's automatic, manual and participating restoration operations use the
same lock. There is no timeout-based lock stealing. An error or timeout alone is
not proof that Upgrade World has stopped: both its operation queue and execution
coroutine must be idle. Ordinary gameplay, character updates and saves continue.

This is **overlap prevention, not a dependency on a successful daily reset**.
If FreshWorld fails but safely ends and finishes cleanup, a later Upgrade World
job may run. If Upgrade World obtains the lock first, FreshWorld waits. There is
no comparison of daily slots, result journals, dates or past successful runs.

Only ServerManager-tracked Upgrade World cron jobs participate. Direct console
commands, Discord commands dispatched outside cron, and other mods' world changes
are not intercepted. Do not start independent world changes during maintenance.
Upgrade World itself needs no patch for this integration.

## Saves

ServerManager does **not** add saves before or after maintenance, including after
partial failures. These existing save paths remain:

- FreshWorld's pre-reset save and its completion barrier.
- Explicit `save` entries in `cron.yml`.
- The game's normal saves.

Example with an explicit pre-save, if desired:

```yaml
timezone: local
jobs:
  - schedule: '45 11 * * *'
    commands:
      - save
      - zones_generate locations=StartTemple:teleporter force start
```

A pre-save preserves the earlier world state; it does not automatically undo a
failed reset. Without a post-save, completed changes may be lost if the server
crashes before the next normal or explicit save. The lock is not a transaction
or a backup. Existing FreshWorld residual-object diagnostics and safety stops
are independent of this coordination.

Explicit dedicated maintenance saves retain ServerManager's headless save path,
including world/session, authorization, cancellation, readiness, busy-state,
save-block and disk-reserve checks. Listen-host and ordinary save behavior are
not replaced. No save-success notification is synthesized.

## Compatibility and failure scope

The optional public contract is `FreshWorld.API.MaintenanceCoordination`, version
2, with `atomic-lease-v1`, `main-thread-v1` and `lease-validation-v1` capabilities.
The consumer checks public signatures and capabilities, not a whole-DLL hash or
exact mod version. Calls run on Unity's thread. Lease ownership is bound to the
current world, FreshWorld session, owner and opaque token.

FreshWorld exposes `TryAcquire(worldId, owner)`, `ValidateLease(...)` and
`ReleaseLease(...)`. Acquisition checks the real gate and pending cleanup together;
an earlier idle query is not authorization. Preparation still invalidates
FreshWorld-owned restoration snapshots before external world changes.

- FreshWorld absent: preserve ordinary Upgrade World scheduling.
- FreshWorld busy: wait without starting a world change.
- FreshWorld installed but unsupported, not ready, or replaced: do not dispatch
  the affected maintenance uncoordinated. Ordinary gameplay and saves remain
  available.
- Upgrade World fails or has uncertain completion: stop the remaining commands,
  observe until idle, and require review. Do not retry or save automatically.
- Lease invalidation, an unobservable executor or same-world suspension: do not
  force-release ownership while native work might remain. Inspect the logs and
  world; a controlled restart may be needed.

This integration does not repair a FreshWorld reset error or certify a safe
Upgrade World command. Back up the world and test destructive commands first.

## Progress and recovery

`cron_last.yml` (or the existing `.yaml` alternative) keeps its ordinary version-1
execution journal. No cross-mod phases, run proofs or lease tokens are added.
Running or uncertain maintenance retains the existing `needs_review` protection
against automatic replay. An in-memory lock is never reconstructed from a file
after restart.

Inspect the world, backups and server log, then use:

```text
sm:cronstatus
sm:cronack <jobId>
```

Discord equivalents are `/cronstatus` and `/cronack job:<jobId>`. Acknowledgement
skips the held occurrence; it does not retry, undo or certify its world changes.
Do not clear existing review markers or delete journals just to install a patch.

The earlier experimental `requiresFreshWorld` mapping and `coordination` journal
extension are not supported or migrated. Unsupported files are rejected rather
than silently overwritten. Keep any recovery evidence and review it before
adjusting an experimental configuration or journal.

## Validation boundaries

Focused tests cover parser/journal stability, public contract validation, lock
ownership, waiting, timeout retention, explicit-only saves and failure handling.
Source-linked harnesses and managed Mono contract probes are not live Unity
world tests.

```powershell
pwsh -File Tests/ServerScheduleSmoke.ps1
pwsh -File Tests/ServerScheduleJournalSmoke.ps1
pwsh -File Tests/ServerScheduleRuntimeSmoke.ps1
pwsh -File Tests/MaintenanceSaveSmoke.ps1
pwsh -File Tests/UpgradeWorldScheduleSmoke.ps1 -UpgradeWorldPath <actual-UpgradeWorld.dll>
pwsh -File Tests/FreshWorldContractMonoSmoke.ps1 -FreshWorldPath <new-FreshWorld.dll>
```

Before production use, test a copied world on the target dedicated server or
listen host: overlapping schedules, FreshWorld failure and cleanup, Upgrade World
partial failure, explicit save delays, long-running operations, shutdown and
configuration reload. Confirm normal gameplay and save behavior remain available.
Static/isolated tests do not establish live Windows, Linux or Docker compatibility.
