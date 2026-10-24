# C4 — Freestanding compilation in CosmoOS (plan and report)

Status: **complete 2026-10-24** (results below). Roadmap acceptance: *a Nova kernel-profile object built
with kernel flags; symbols, relocations and sections checked against the module loader's allowlist;
frame sizes at most 2 KiB; no FP registers (disassembly check)* (`roadmap.md`; prompt
`cosmoos-native.md` C4: build a minimal Nova object using no libc or hosted runtime, inspect its
symbols, relocations and runtime dependencies, and prove the freestanding restrictions). Stage S0 as in
C1–C3: out of tree, CosmoOS read-only. Script: `integrations/cosmoos/c4.py`. It builds on M8's
freestanding profile (`cosmoos/profiles.md` §4).

## Audit (CosmoOS `98040b9b`, v1.0.12; read, not copied)

AGENTS.md asks for a re-audit of every interface a C-milestone touches. What C4 relies on, from the
code:

| Interface | Where | What it says |
|---|---|---|
| Module compile flags | `build/module.mk:13`, `build/toolchain.mk:27-75`, `build/arch/<arch>.mk` | `MODULE_CFLAGS` = `KERNEL_CFLAGS` + `-DCOSMO_MODULE_BUILD=1`: `-std=c11 -ffreestanding -nostdlib -nostdinc -fno-stack-protector -fno-asynchronous-unwind-tables -fno-unwind-tables`, the warning set with `-Werror`, `-O2 -g` (release), `-Wframe-larger-than=2048`; x86-64 `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only -fno-pic -fno-pie`; AArch64 `-march=armv8-a -mcmodel=small -mgeneral-regs-only -mno-outline-atomics -mno-omit-leaf-frame-pointer -ffixed-x18 -fno-pic -fno-pie`. `c4.py` asks Make for them |
| Module link | `build/module.mk:14,72` | `ld.lld -r --no-dynamic-linker -z noexecstack --build-id=none`, then `scripts/modsign.py` and `scripts/check-module-elf.py` |
| ELF validation | `kernel/module/modelf.c:76-215` (`modelf_validate`) | ET_REL, native machine, no REL (RELA only), no dynamic symbols, allocatable sections only PROGBITS or NOBITS, W^X, no executable NOBITS, alignment a power of two up to a page, no COMMON or XINDEX symbols, no undefined local symbols; plus the `.cosmo.module` metadata (240 bytes, read-only) |
| Relocations | `kernel/arch/x86_64/modreloc.c:39-81`, `kernel/arch/aarch64/modreloc.c:57-190` | x86-64: `R_X86_64_NONE`, `64`, `PC32`, `PLT32`, `32`, `32S`; AArch64: 21 (two `NONE` forms, absolute 16/32/64, PC-relative 16/32/64 and LO21, page HI21 and the low-12 forms, `LD_PREL_LO19`, branches `TSTBR14`, `CONDBR19`, `JUMP26`, `CALL26`); anything else is `-ENOEXEC` |
| Kernel exports | `EXPORT_SYMBOL(...)` across the sources (e.g. `kernel/core/panic.c:131`, `kernel/core/string.c:141-144`) | `panic`, `memcpy`, `memmove`, `memset`, `memcmp` are exported, so a module may call them |
| No FP or vector registers | `scripts/check-fpregs.sh`, run on the kernel image by `Makefile:567` | an `objdump -d` scan for `%xmm`/`%ymm`/`%zmm`/`%st(` (x86-64) and `v`/`q`/`d`/`s` registers (AArch64) outside the FPU save and restore |
| Panic | `kernel/include/kernel/panic.h:31` | `void panic(const char *fmt, ...) __noreturn __printf(1, 2)`; its `printf` has `%s` and `%u` (`kernel/core/printf.c:239,267`) |

One disagreement with Nova's earlier docs, now fixed: `cosmoos/abi.md` said the allowlist has "no
GOT/TLS/PLT", but `modreloc.c` accepts `R_X86_64_PLT32`, which clang emits for a direct call (the
C4 object uses it). `abi.md` now points at the exact list above. `profiles.md` §3 agrees with the
code.

## What was built

- **`integrations/cosmoos/c4/kdemo.nova`** (`--profile freestanding`): five exports with the shapes
  kernel code has: CRC-32C over raw bytes (loops, shifts, checked arithmetic, `Slice.fromRaw`), a
  device reset through `MmioPtr<DeviceRegs>` and `Volatile<u32>` registers with `Cpu.spinHint()`, a
  command decoder (enums with payloads, optionals, a `FixedBufferAllocator` over a stack buffer), a
  loop calling a C callback through an `extern "C" fn` pointer, and a checked add whose overflow panics.
- **`integrations/cosmoos/c4/nova_rt_kernel.c`**: the environment's `nova_panic` for kernel code
  (`profiles.md` §3.3, §4.2): `panic("nova panic: %s at %s:%u:%u", nova_panic_reason(kind), …)`.
  `nova_panic_reason` moved into the freestanding core for this (it was the hosted runtime's
  private table); the hosted runtime uses it too.
- **`c4.py`** compiles the generated `main.c`, the freestanding `nova_rt_core.c` and the binding with
  CosmoOS's `MODULE_CFLAGS` plus `-fstack-usage`, joins them with its `ld.lld` and `MODULE_LDFLAGS`, and
  checks the result. It reads the relocation types from `modreloc.c` and the exports from the sources,
  so a CosmoOS change shows up as a failure instead of going unnoticed.

## Results (run 2026-10-24, both arches)

| Check | x86-64 | AArch64 |
|---|---|---|
| Compiles with `MODULE_CFLAGS` (`-Werror`, CosmoOS's warnings, `-Wframe-larger-than=2048`, `-mgeneral-regs-only`) | pass | pass |
| `modelf.c` ELF rules (all but the module metadata) | pass | pass |
| Relocations | `R_X86_64_32S`, `64`, `PLT32`: all applied by `modreloc.c` | `R_AARCH64_ABS64`, `ADD_ABS_LO12_NC`, `ADR_PREL_PG_HI21`, `CALL26`: all applied |
| Undefined symbols | `memset` (a compiler-emitted zeroing), `panic` (from the binding): both exported | `panic`: exported |
| No `.init_array`/`.fini_array`/`.ctors`/`.dtors`, `.tdata`/`.tbss`, `.eh_frame`; empty `.data`/`.bss` | pass | pass |
| Largest frame (`-fstack-usage`; limit 2048, all static) | 312 bytes (`kdemo_execute`) | 336 bytes (`kdemo_execute`) |
| `scripts/check-fpregs.sh` | pass | pass |
| CosmoOS working tree unchanged | pass | pass |

Global symbols of the object: the five exports, `nova_panic`, `nova_panic_reason`, and the core's
`nova_fba_over`, `nova_region_alloc`, `nova_io_message`, `nova_utf8_valid`. No libc and no hosted
runtime: the only dependencies are the two kernel exports above.

**Negative controls** (each must be rejected, or a pass would mean nothing): a floating-point function
built without `-mgeneral-regs-only` (`check-fpregs.sh` reports `%xmm0` / `d0, d1`); a COMMON symbol;
thread-local storage (`.tbss`, and TLS relocations not in the allowlist); mutable global state
(`.data`); unwind tables (`.eh_frame`); position-independent code (GOT relocations, types 42 and
311/312); an undefined symbol the kernel does not export; a 4 KiB stack frame (the compiler's
`-Wframe-larger-than` error). **8 of 8 rejected on both arches.**

## What this proves, and what it does not

- Proved: Nova's freestanding output is kernel-compatible C. It compiles warning-free under the
  kernel's exact flags, needs nothing but exported kernel symbols, and meets every structural rule
  the module loader checks before it relocates anything.
- Not done here: loading the object. A module also needs `COSMO_MODULE` metadata, `init`/`exit`,
  and a signature, and runs through the loader's relocation and symbol resolution (C8). The
  kernel profile's language rules (no panicking allocation, the `kernel-panic-sites` lint,
  `@noPanic`, context effects; `profiles.md` §3.1) are C5–C7. `.su` files show static frames; the
  whole-call-chain bound (16 KiB) is later work (`profiles.md` §1).
- Kernel-profile allocation (`KernelHeap`) needs user-defined allocators (C5).
