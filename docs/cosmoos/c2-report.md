# C2 — Safe allocation in CosmoOS (plan and report)

Status: **complete 2026-10-22** (results below). Roadmap acceptance: *M2+M3 features in Profile A; allocation,
destruction and fault-injection tests pass inside QEMU on both arches.* Stage S0 as in C1: out of
tree, CosmoOS read-only (`build-integration.md`). Script: `integrations/cosmoos/c2.py`.

## Plan

1. **Allocation and destruction in the guest.** Every run-pass and run-fail program runs inside
   CosmoOS with the expected stdout, exit status and panic message. This includes the M3 heap
   programs (`boxes`, `heap-error-paths`, `ex4`, `ex5`, `ex14`, `deep-structures`, `heap-stress`).
   `SystemHeap` is CosmoOS libc `malloc`/`free` through the hosted runtime layer
   (`profiles.md`, Profile A).
2. **Guest-side fault-injection sweep.** For each run-pass program that allocates:
   - *Baseline:* run it in the guest with `NOVA_ALLOC_REPORT=1`. The guest must report the same
     number of allocation attempts *n* as the host run (allocation behaviour is deterministic and
     platform-independent), and `live=0`.
   - *Sweep:* run it with `NOVA_FAIL_ALLOC_AT=k` for each `k ≤ n`. For *n* > 40, sample: the
     first 10, the last 10 and 20 evenly spaced. Every run must end either with the report
     `live=0`, or with the panic "unwrapped an Err value" and status 134; no other panic.
3. **Allocator stress:** `run-pass/heap-stress.nova` builds and destroys many lists (about 10⁵
   allocations), so CosmoOS's allocator reuses and coalesces freed memory under Nova's
   allocation pattern. It must end with `live=0` in the guest and on the host.
4. **Unchanged CosmoOS:** `git status` of CosmoOS is the same before and after.

Not in C2:
- **Real memory exhaustion.** CosmoOS anonymous `mmap` creates a VM mapping;
  `kernel/syscall/native.c:440-540` returns `-ENOMEM` only when no address range is free. Pages
  appear to be supplied on first touch (inferred, not verified by a run), so exhausting physical
  memory would likely end in a fault rather than a NULL from `malloc`. Exhaustion behaviour
  belongs to the kernel profile (C5, `KernelHeap`) and to an owner-approved CosmoOS change, if
  any.
- **Over-aligned layouts** (`> 16`): Nova v0 has none; `Box.create` statically asserts
  `_Alignof(T) <= _Alignof(max_align_t)` (`c-backend.md`).

## Findings while planning (CosmoOS code read, not changed)

- **Shell assignments leak.** `userland/shell/sh.c:668-676` handles `NAME=value cmd`, but its
  comment says "exported to the child only (simplified: set)": the variable is `setenv`'d in the
  shell itself and stays set for later commands. `c2.py` therefore sets both variables on
  every sweep command and runs the plain corpus first.

## Results (run 2026-10-22)

CosmoOS `c4ac584b` (read-only), release build, QEMU TCG, `integrations/cosmoos/c2.py`:

| Check | x86-64 | AArch64 |
|---|---|---|
| Corpus inside the guest (111 run-pass and run-fail programs: stdout, exit status, panics) | PASS | PASS |
| Guest fault-injection sweep: 7 allocating programs, 123 runs | PASS | PASS |
| Baseline attempt counts equal the host's (e.g. `heap-stress` 105,030; `deep-structures` 600,000), `live=0` | PASS | PASS |
| Clean shutdown (QEMU exit status 33) | PASS | PASS |
| CosmoOS working tree unchanged | PASS | PASS |

Swept programs and their allocation attempts:
- swept at every point: `boxes` (9), `ex14-tree` (8), `ex4-account` (1), `ex5-allocator` (3),
  `heap-error-paths` (15);
- sampled at 40 points: `deep-structures` (600,000), `heap-stress` (105,030).

Sample from the AArch64 serial log (`out/cosmoos/c2-aarch64/serial.log`):

```text
cosmo$ NOVA_ALLOC_REPORT=1 NOVA_FAIL_ALLOC_AT=0 nv-heap-stress
91680
nova alloc: attempts=105030 live=0
```

The host sweep (`FaultInjectionTests`, ASan+UBSan) checks the same programs at every point up
to 200 attempts. The guest sweep confirms the same behaviour with CosmoOS's `malloc`, `free`,
`mmap`-backed arenas and process exit.

**C2 acceptance is met** on both architectures. Open items, carried forward:
- real-exhaustion behaviour of userland `malloc` (see "Not in C2");
- the kernel profile's allocator (C5).

## SSH mode: the Parallels VM (added 2026-10-22, during M6b)

`c2.py --arch aarch64 --ssh root@10.211.55.14` runs the same checks on an already running CosmoOS
machine over ssh, here the aarch64 VM "CosmoOS 1.0.7" in Parallels Desktop:
- all ELFs are copied to `/tmp/nova` (a ramfs) with one `scp`;
- one ssh session runs every program with `env -i` plus the job's variables, and keeps each
  program's stdout, stderr and exit status apart;
- the checks are those of the QEMU path, applied to separate streams. That is stricter: any
  stderr output from a program that should not panic is a failure;
- nothing on the machine changes except `/tmp/nova`, and there is no shutdown check.

A first version drove the VM's serial console and fetched the programs with `wget`; ssh replaced
it once root ssh access was set up.

Facts recorded on 2026-10-22:
- The VM named "CosmoOS 1.0.7" reports `6.0.0-cosmo CosmoOS 0.0.1 v1.0.10 aarch64`, so its name
  probably predates an upgrade (inferred). Nova's ELFs, linked against CosmoOS `c4ac584b`'s libc,
  ran unchanged.
- **ssh was not available at first, and was fixed in the VM.** `svc status` reported `dropbear`
  running as pid 13, but that pid had been reused by `syslogd`, and nothing listened on port
  22. `svc start dropbear` refused ("already running"). `svc restart` would have signalled the
  reused pid, so dropbear was started by hand (`/sbin/dropbear -p 22 -r <its host key>`), outside
  svc. **CosmoOS finding:** svc trusts a stale pid without checking that it is still its process.
- `/root/.ssh/authorized_keys` was empty; the host's `~/.ssh/id_rsa.pub` was appended (owner's
  instruction: root ssh with that key).
- **CosmoOS finding:** on `/tmp` (ramfs), busybox `rm -rf` of a directory with many entries left
  some behind ("Directory not empty"); entries are probably skipped while deleting during a
  directory read (inferred). `c2.py` repeats the removal until the directory is gone.
- The VM's busybox shell prints `Aborted` after an abort (only relevant to the serial version).

| Run | Result | Wall time |
|---|---|---|
| ssh, Parallels VM, aarch64 (M6b corpus) | 120/120 programs, 179/179 sweep runs | 50 s, against about 15 min per arch in QEMU TCG |

x86-64 has no such VM on this host, so it still runs in QEMU (`c2.py --arch x86_64`).
