# CosmoOS Integration — Feasibility Report and First-Integration Recommendation

Status: C0 (deliverables 1 and 12). Based on read-only audits of CosmoOS HEAD
`30d34b29` (branch `b2-cosmofs-defects`, v1.0.9-11) and main `4527d16d` (v1.0.9).
**Nothing was built, booted or modified in CosmoOS during C0.**

## Verdict

| Area | Feasible? | Conditions |
|---|---|---|
| Profile A (native userland) | **Yes, now, with no CosmoOS changes** (S0) | Public libc only (OD-15). No constructors. `crt0.o` always linked. Generated C must need no compiler-rt (AArch64 outline atomics to be verified) |
| Profile A in-tree (port) | Yes, mechanically: one `ports/` directory, no Makefile edits | Owner must choose it as a unit (AGENTS.md) and accept checked-in generated C (OD-19, OD-20) |
| Profile B built-in kernel code | Yes, technically | Exact kernel flags. No floats. 2 KiB frames and a 16 KiB stack. Include CosmoOS headers, never re-declare. Kernel-side units need owner approval |
| Profile B loadable modules | Yes, with known gaps | `preempt_enable` not exported (KQ-2) means no explicit quiesce read sections in modules. No `call_quiesce` barrier (KQ-3). Callbacks not tracked at unload, so Nova must generate teardown |
| Safety value for CosmoOS | High where it matters | Removes the panic-on-misuse class (double free, wrong cache or order). Adds compile-time checks CosmoOS lacks: typed addresses, `kobject_put` under a spinlock, guard escape |

## Main obstacles (ranked)

1. **Governance:** CosmoOS work is restricted to the owner-chosen unit, and the
   plan discourages new languages without a concrete benefit. Mitigation: prove C1 out of tree, then
   propose a minimal unit backed by evidence.
2. **Thin native libc** (no math, poll, DNS, `aligned_alloc`, exec, setjmp) limits
   C3-class utilities. Mitigation: pick C3 tools within the existing surface, and propose libc
   additions as separate units.
3. **Context rules are runtime properties.** Nova's effects can only approximate them, and lockdep
   plus `might_sleep` stay authoritative.
4. **CI budget** on AArch64.

## Recommendation for the first working integration

**C1 via route S0** (`build-integration.md` §3), executed after M1:

1. `novac` compiles `hello.nova` (prints `NOVATEST: PASS`, exits 0) to C with the
   `cosmoos-user` profile.
2. The script builds CosmoOS `libc.a` + `crt0.o` with the existing `gmake ARCH=<a> libc`.
   Writes go only to CosmoOS's `out/`, and no source file changes.
3. The script compiles and links with CosmoOS's exact `USER_CFLAGS`/`USER_LDFLAGS`, then checks
   `.note.cosmo`, W^X segments and the undefined-symbol allowlist.
4. The binary is placed on a separate disk image passed via `QEMU_EXTRA` (or a
   release image's scratch disk; this must be verified first, because debug self-tests format the
   default test disk). CosmoOS boots in QEMU, the program runs from the shell, and the marker is observed.
5. Repeat for AArch64.
6. Record flags, versions and hashes in `integrations/cosmoos/c1-report.md`.
   Afterwards `git status` in CosmoOS must be clean.

Then present the C1 report to the CosmoOS owner with the S1 proposal
(`ports/nova-hello/` + one `rc.test` line + one boot-test marker).
