# CosmoOS ABI Compatibility Report

Status: C0 (deliverable 4). Every row is either **verified** in the audits (cited) or
**to verify** (it needs a build or run, which C0 did not do). [PA] =
`research/cosmoos-platform-audit.md`, [KA] = `research/cosmoos-kernel-audit.md`.
CosmoOS revision read: HEAD `30d34b29`, main `4527d16d` (v1.0.9).

## 1. Contracts Nova must honour

### Userland (Profile A)

| Contract | Value | Nova consequence | Status |
|---|---|---|---|
| Target triples | `x86_64-unknown-none-elf`, `aarch64-unknown-none-elf` [PA §3] | `TargetInfo` entries `x86_64-cosmoos`, `aarch64-cosmoos` | verified (docs/Make) |
| Data model | LP64, max align 16, `char` signed (x86-64) / unsigned (AArch64) | Layout from `TargetInfo`; `_Static_assert`s in generated C catch mismatches | verified (clang predefines) |
| C dialect / flags | `USER_CFLAGS` (`build/toolchain.mk:27-61`, `libc/libc.mk:12-18`) | Generated C must be warning-clean under them | **verified (C1, 2026-10-22)**: 72 programs × 2 arches, `-Werror` |
| Builtins library | none linked | No libcalls (`c-backend.md` §1) | **verified (C1)**: no undefined symbols on either arch. No outline-atomic helpers were needed |
| Executable format | static ET_EXEC @ 0x400000, `user.ld`, three W^X PT_LOADs, PT_GNU_STACK non-exec | Link only with CosmoOS `USER_LDFLAGS` | verified (link recipe) |
| Personality | `.note.cosmo` from `crt0.o`. A missing note means Linux personality, silently [PA impl 6] | Always link `crt0.o`. CI check with `llvm-readelf -n` | verified |
| Entry | `crt0` → `__libc_start` → `main(argc, argv, envp)`; `exit` flushes stdio | Generated `main`; Nova cleanup done before return | verified |
| Constructors | `user.ld` has no `.init_array` → never run | Nova emits no constructors (static initialisers must be constant) | verified |
| Syscalls | native numbering 0–105; `-errno` in [-4095,-1]; x86-64 `syscall` (rax; rdi, rsi, rdx, r10, r8, r9); AArch64 `svc #0` (x8; x0–x5) [PA §4] | **Not used directly** (L1). Only via libc (OD-15) | verified |
| Errno | libc `errno` (TLS) | `IoError.fromErrno()` reads it immediately after the call | verified |
| Handles/fds | fds are rights-carrying handles | Nova `Fd` is a move type, `drop` = `close` | verified |
| Threads | only `cosmo_thread_*` (libc TCB). Never raw `SYS_thread_create` | Nova threads (later) wrap `cosmo_thread_*` | verified |
| malloc | first-fit, 16-byte aligned, global lock, `NULL`+`ENOMEM` on failure, no `aligned_alloc` | `SystemHeap` handles over-alignment itself | verified |

### Kernel (Profile B)

| Contract | Value | Nova consequence | Status |
|---|---|---|---|
| Compiler | clang only, freestanding, `-nostdinc`, non-PIC | same flags, per config + arch | verified [KA §7] |
| Code model | x86-64 `-mcmodel=kernel -mno-red-zone`; AArch64 `-mcmodel=small -mno-outline-atomics -ffixed-x18` | Take from the CosmoOS build. **x18 reserved on AArch64**, so no inline asm may use it | verified |
| FP/SIMD | forbidden everywhere in the kernel (`-mgeneral-regs-only`; disassembly check on the kernel ELF) | Kernel profile rejects `f32/f64`. Also forbid builtins that may vectorise (clang won't, under `-mgeneral-regs-only`) | verified; module FP check is KQ-7 |
| Stack | 16 KiB per thread, IRQs on the same stack; 2 KiB frame limit (error) | Frame and chain checks (`profiles.md` §1) | verified |
| Module object | ELF64 ET_REL from `ld.lld -r` with `MODULE_CFLAGS`; exactly one 240-byte `.cosmo.module` via `COSMO_MODULE` (ABI **11**); optional `.ksymtab`; RELA only; no W+X, no COMMON; per-arch relocation allowlist (`modreloc.c`: no GOT or TLS relocations; `R_X86_64_PLT32` is accepted, as clang's form of a direct call; AArch64 calls within ±128 MiB); Ed25519 signature via `scripts/modsign.py` | Nova emits C that uses the `COSMO_MODULE` macro from CosmoOS headers, so the 240-byte layout is never hand-written. Link and sign with CosmoOS's own rules | verified (format); **C4 (2026-10-24): a Nova object built with `MODULE_CFLAGS` and `ld.lld -r` meets the ET_REL, RELA, section, W^X, COMMON and relocation rules on both arches** (`c4-report.md`); metadata and signing: C8 |
| Exported-struct layouts | module ABI; changes bump the ABI version | Imported from the CosmoOS headers of the build (ADR 0008): Nova declares only the fields it uses, by C name; generated C accesses the real members and checks each field's type at compile time; Nova never writes a layout | verified (rule); implemented with C6 |
| Symbols | modules import only exported kernel symbols plus declared dependencies | Bindings file lists the symbols. Novac fails before link if a binding is not exported (built from `.ksymtab` data) | to design (C5) |

## 2. Known incompatibilities and gaps

1. `preempt_enable` is not exported → modules cannot use inline
   `quiesce_read_unlock` (KQ-2). Built-in code is unaffected.
2. No `call_quiesce` barrier at module unload (KQ-3) → Nova modules may not
   leave quiesce callbacks pending in module text.
3. ~~AArch64 userland may emit outline-atomic helpers~~: checked in C1. None are emitted for
   Nova's M1 programs (no atomics). This must be rechecked when Nova gains atomics.
4. `limits.h` `CHAR_MIN/MAX` are wrong on AArch64 (CosmoOS bug; Nova
   unaffected because it never uses `char`). **Report to the owner**; do not fix it from
   the Nova side.

## 3. Non-goals

- Nova does not define a new syscall, a new ELF note, a new module section or
  a new loader feature. If any of these is ever needed, it is a separate CosmoOS
  unit proposal.
- No .NET component ever runs inside CosmoOS.
