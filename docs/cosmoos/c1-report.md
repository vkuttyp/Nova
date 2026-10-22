# C1 Report — Nova programs running natively in CosmoOS

Date: 2026-10-22. Status: **C1 met on x86-64 and AArch64** (stage S0, out of tree).
Script: `integrations/cosmoos/c1.py` (`--corpus`). Logs: `out/cosmoos/c1-<arch>/` (not committed).

## What ran

| Item | Value |
|---|---|
| CosmoOS revision | `30d34b29` (v1.0.9-11, branch `b2-cosmofs-defects`; its differences from `main` `4527d16d` are cosmofs-only) |
| CosmoOS build | `gmake ARCH=<arch> BUILD=release OUT=<Nova>/out/cosmoos/<arch>-release image` (~4.5 min for x86-64) |
| Compiler | Apple clang 21.0.0 (CosmoOS's `CC`), `ld.lld` from the host toolchain (CosmoOS's `LD`) |
| novac | `0.1.0-dev`, commit `318f07d` plus the C1 fixes in this change |
| Host | macOS on Apple silicon, QEMU TCG (`qemu-system-x86_64` q35; `qemu-system-aarch64` virt, cortex-a72) |

The exact flags are recorded in `out/cosmoos/c1-<arch>/flags.txt`. They come from CosmoOS's Make
(`USER_CFLAGS`, `USER_LDFLAGS`); none was copied by hand. For x86-64 they include `-std=c11
-ffreestanding -nostdinc -fno-strict-aliasing -fno-pic -fno-pie -O2 -Werror -Wall -Wextra -Wshadow -Wvla
-Wundef -Wpointer-arith -Wwrite-strings -Wstrict-prototypes -Wmissing-declarations
-Wimplicit-fallthrough -Wcast-qual -Wformat=2 -Wdate-time`. They link
statically with `-T userland/user.ld crt0.o … libc.a`.

## Results (both architectures)

| Check | x86-64 | AArch64 |
|---|---|---|
| Generated C and the Nova runtime compile under the exact `USER_CFLAGS` (with `-Werror`) | pass (72 programs) | pass (72 programs) |
| Link against CosmoOS `crt0.o` + `libc.a`, with no undefined symbols and no compiler-rt needed | pass | pass: **no outline-atomic helpers** were required (an open question in the platform audit) |
| ELF: `.note.cosmo` (native personality), W^X PT_LOADs, non-executable PT_GNU_STACK | pass | pass |
| `nova-hello` prints `NOVATEST: PASS`, `gcd(1071, 462) = 21`, `i64 min = -9223372036854775808` | pass | pass |
| `nova-panic` prints its output, then `nova panic: integer overflow at nova-panic.nova:5:23`; the kernel logs exit status 134 | pass | pass |
| **Whole run corpus in the guest**: 51 run-pass and 19 run-fail programs, with stdout, kernel-logged exit status and panic location matching the host expectations | **70/70** | **70/70** |
| CosmoOS shuts down cleanly (QEMU exit 33) | pass | pass |
| CosmoOS `git status --porcelain` unchanged | pass | pass |

Serial excerpt (x86-64):

```text
cosmo$ nova-hello
[ INFO] process: pid 17 'nova-hello' created, entry 0x0000000000400190, 3 segments
Hello from Nova on CosmoOS
gcd(1071, 462) = 21
i64 min = -9223372036854775808
NOVATEST: PASS
[ INFO] process: pid 17 'nova-hello' exited with status 0 (6 syscalls)
cosmo$ nova-panic
NOVAPANIC: before
nova panic: integer overflow at nova-panic.nova:5:23
abort()
[ INFO] process: pid 18 'nova-panic' exited with status 134 (57 syscalls)
```

The in-guest checker was mutation-tested against the real serial log: a wrong expected stdout,
exit status or panic line are each reported.

## How the image was made without touching CosmoOS

1. CosmoOS's own Make built the release image into Nova's `out/` (`OUT=`).
2. The script read that boot archive (`boot.tar`, ustar) and rebuilt it with CosmoOS's
   `scripts/mkbootarchive.py`: all original entries in order, plus `bin/nova-*`.
3. It signed a new manifest with `scripts/mkmanifest.py` and the developer key (`MODSIGN_KEY`),
   and built the image with `scripts/mkimage.sh`.
4. It booted with `scripts/qemu-run.sh` (`QEMU_ARCH` selects the machine) and typed each program's
   name at the `cosmo$` prompt, as CosmoOS's own shell harness does.

## Problems found and fixed along the way

- `llvm-readelf` is not on this host's PATH, so the ELF checks now parse the headers in Python.
- `qemu-run.sh` selects the machine with `QEMU_ARCH`, not `ARCH`. The first AArch64 attempt
  booted the AArch64 image in x86 QEMU, and the script caught it (no prompt).
- novac must receive *relative* source paths, or host paths end up in panic messages.

## What C1 does not show

- No CosmoOS CI job runs this. It is a local, manual run (stage S0). Moving it in-tree (stage S1:
  `ports/nova-hello/` plus one `rc.test` line) needs the owner to choose it as a unit (OD-20).
- Only release images were booted. The debug image (with `SELFTEST`) was not tried.
- The programs use only `write`/`exit`/`abort` through libc stdio. No files, sockets or threads (C3).
