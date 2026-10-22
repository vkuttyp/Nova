# CosmoOS platform audit for Nova (Profile A userland and build integration)

- Tree audited: `~/dev/CosmoOS`, branch `b2-cosmofs-defects`
- HEAD: `30d34b29da1f913716cca57004a966954477aca3` (`git describe`: `v1.0.9-11-g30d34b29`)
- main: `4527d16d2394de30b1d5b0241618bf08eb78f034` (`git describe`: `v1.0.9`)
- The branch is main plus 11 cosmofs-only commits (B2 defects). Nothing that this audit touches differs from main.
- Untracked files in the tree at audit time: `Todo.md`, `prompts/prompt-oct-5.md`, `prompts/promt-oct-8.md`.
- Date: 2026-10-22. Read-only audit. Nothing was built, booted, committed or modified. `make -n` was **not** run either, because
  `Makefile:113` writes a file through `$(file >...)` during recipe expansion, and GNU make expands recipes even under `-n`.
- Citations are `path:line`, relative to the CosmoOS root unless the path says otherwise. "(inferred)" marks a conclusion that
  is not stated in the tree.
- Brief: `/Users/kutty/Nova/prompts/cosmoos-native.md` (§2 Profile A at lines 56-80, §7 at 259-281, §8 at 283-300,
  §9 at 302-327, §11 at 425-443).

---

## 1. Repository rules that apply to an external toolchain

### 1.1 Governing documents and precedence

- `AGENTS.md:3-5`: read `prompts/` first ("the constitution"), then follow `docs/development.md#workflow-for-a-change`,
  `docs/backlog.md` and the current task prompt.
- `docs/README.md:3-8`: the master prompt in `prompts/` is the constitution. "When a document here and the constitution
  disagree, the constitution wins and the document is wrong."
- Constitution §50 "Coding Language Strategy" (`prompts/MASTER PROMPT — ...md:1637-1668`):
  - C for the generic kernel (`:1643`).
  - Assembly only where it cannot be avoided (`:1647-1655`).
  - "Rust may later be introduced selectively ... but do not introduce multiple implementation languages until the kernel
    architecture is stable" (`:1657`).
  - "Do NOT use C#/.NET for the kernel core" (`:1659`).
  - C#/.NET "may be used later for: host development tools, package tooling, image builders, debugging tools, management
    utilities, optional user-space applications" (`:1661-1668`).
  - **So a .NET-hosted Nova compiler is a "host development tool" and is explicitly allowed.** A .NET runtime inside
    CosmoOS is not part of what Nova needs.
- Constitution §51 C coding standards (`:1672-1690`): C11 or a restricted modern C, explicit integer widths, `stdint.h` and
  `stdbool.h`, warnings as errors, static analysis, no intentional UB, explicit ownership and lifetimes, checked arithmetic.
  Nova-generated C should be written to meet this bar (inferred).
- `docs/plan.md:767`: the row "~~Rust or C#/.NET adoption~~" is struck as obsolete, with the decision text "introduce another
  language only for a concrete benefit ... explicitly not a deliverable; decision recorded" (2026-10-20). **There is no
  sanctioned slot for a new language today.** Bringing Nova into the tree is an owner decision.
- `tools/README.md:3`: "Host-side development tools ... May use languages other than C." Today's tools are Python
  (`tools/*.py`, `scripts/*.py`).
- Constitution §64 (`:2080-2090`): every major subsystem has `architecture.md`, `design.md`, `api.md`, `invariants.md` and
  `testing.md`. That pattern holds in practice, e.g. `docs/libc/{api,architecture,design,invariants,testing}.md` and
  `docs/build/*`. An in-tree Nova component would be expected to carry the same five documents (inferred).
- Constitution §65 AI coding rules (`:2109-2159`): never invent an API that conflicts with a contract; do not silently change
  the ABI; do not hide allocations or synchronisation; stop and explain an architectural problem.
- Constitution §66 workflow (`:2163-2191`): inspect → interfaces → design → invariants → risks → data structures → APIs →
  implement → tests → diagnostics → compile → static analysis → QEMU tests → reviews → document.
- Constitution Invariant 7 (`:1972-1974`): "Linux compatibility must not contaminate the native ABI." `compat/README.md:3`
  restates it.

### 1.2 Scope rules (they block unsolicited work)

- `AGENTS.md:9-12`: the 1.2 roadmap (direction B, storage depth) sets the work. The 1.0 and 1.1 roadmaps are complete.
- `AGENTS.md:14-22`:
  - Work only on the roadmap unit the owner has chosen. If none is chosen, propose one and do not start it.
  - A defect found along the way is fixed only if it blocks that unit's acceptance test or turns main red. Otherwise it gets
    one backlog line.
  - `docs/plan.md` and the deferred-work inventory are historical; nothing new is added to them.
- `AGENTS.md:23-24`: a new feature is proven by its acceptance test. The probe/`--old` convention applies to defect fixes,
  not features.
- `AGENTS.md:31`: at most 5 PRs per unit.
- `docs/backlog.md:14-17`: a backlog line is taken up only as part of a chosen roadmap unit.
- **Consequence for Nova (inferred).** Nova integration is not a roadmap-1.2 unit (see §2), so any in-tree change, even a
  Make target, needs the owner to choose it as a unit, or it must live out of tree. The brief's "preserve the existing build
  workflow" (`cosmoos-native.md:302-327`) fits an out-of-tree-first approach.

### 1.3 Conventions, review and test gates

- `AGENTS.md:37-41`: one unit = one branch from current main = one PR. Record scope and baseline, update design docs and
  invariants before the smallest correct change, validate, then update the backlog, `docs/history/subsystem-units.md` and a
  dated audit report.
- `AGENTS.md:42-45`: a defect fix needs a deterministic failing test and a `tools/<name>-probe.py` with an `--old` mode, on
  both architectures.
- `AGENTS.md:46-51`: never weaken assertions or widen budgets; unexplained failures are regressions (`docs/testing/flakes.md`).
- `AGENTS.md:52-55`: instrumentation goes under `CONFIG_DEBUG`/`CONFIG_SELFTEST`.
- Validation matrix, `AGENTS.md:62-76`, on x86-64 and AArch64:
  - `make host-test`, `make fuzz`, `make analyze`;
  - debug `make test` at `QEMU_SMP=1/2/4`;
  - `test-smp2`, `test-chaos`, `test-harness-retry`;
  - `make BUILD=release test`;
  - branch CI green on both architectures.
  - GNU make (`gmake` on macOS).
- `AGENTS.md:25-30`: the matrix runs once per PR. Documentation-only PRs are exempt.
- `docs/development.md:666-676`: feature branch plus PR; Greptile reviews every PR. Before pushing: `make test`,
  `make BUILD=release test`, `host-test`, `host-test-fetch`, `fuzz`, `analyze`, `reproducible`, `test-crash`. Subsystem docs
  are updated in the same PR.
- `reviewer.yaml:1-13`: CosmoReview also reviews every PR. Its language list is
  `[c, asm, shell, markdown, python, yaml, json, make, ld]` (`reviewer.yaml:13`). A `.nova` or `.cs` file would be outside it
  (inferred).
- Commit style, from `git log main`: `<subsystem>: <lower-case sentence>` (e.g. "pkg: a downgrade is reported as a downgrade
  (PKG-9)"), merged through GitHub merge commits ("Merge pull request #432 ..."). No `Co-Authored-By` convention is visible in
  the subjects.
- Build invariants (`docs/build/invariants.md`) that any generated code must respect:
  - **B1** (`:6-11`): an explicit `--target=` triple on every target compile.
  - **B2** (`:13-19`): `-nostdinc`; only clang's resource dir is added back, so no host headers.
  - **B4** (`:28-32`): `-Werror` is never removed per file or component; "a warning that cannot be fixed is fixed by changing
    the code, not the flags".
  - **B5** (`:34-38`): output never lands in the source tree. **Generated C must go under `$(OUT)`.**
  - **B6** (`:40-46`): identical sources give identical binaries (`-ffile-prefix-map`, `-Wdate-time`, `SOURCE_DATE_EPOCH`).
    **The Nova compiler must be deterministic.**
  - **B12** (`:83-88`): sources are listed explicitly, with no globbing.
  - Analyzer gate (`docs/build/design.md:120-133`): an exact compiler version must be in the reviewed baseline, or the gate
    fails. The baselines name "Apple clang version 21.0.0 (clang-2100.3.34.2)" and "Debian clang version 19.1.7 (3+b1)"
    (`tools/analysis/x86_64.json`, `tools/analysis/aarch64.json`, key `compilers[].version`).

### 1.4 Where third-party and ported software lives

There are two mechanisms.

1. **Declarative recipes**: `ports/<dir>/port` plus sources (`ports/README.md:3-9`). `make ports` cross-compiles them with
   the *native* userland toolchain into signed `.cpk` packages and a signed `INDEX` (`pkg/pkg.mk:35-65`).
   - Directories are discovered by wildcard: `PORT_DIRS := $(sort $(dir $(wildcard $(ROOT)/ports/*/port)))`
     (`pkg/pkg.mk:37`). This is the one place where adding a directory needs no Makefile edit.
   - Examples: `ports/hello`, `hello-1.0`, `fortune`, `fortunes`, `demolib-*`, `demo-[a-d]`, `libdemo`.
2. **Makefile-driven upstream builds** of pinned tarballs (`scripts/fetch-pinned.py`, SHA-256 plus a mirror):
   - `ports/busybox/busybox.mk`: BusyBox 1.37.0, musl 1.2.5 and compiler-rt 19.1.7 (`:10-18`);
   - `ports/mbedtls/mbedtls.mk` (mbedTLS 3.6.7, `:11-13`), `ports/dropbear`, `ports/openssh`.
   - These are **Linux-personality** programs built against musl (§4.8), included from `Makefile:53-56`.
   - Build trees live beside `$(OUT)`, not in it: `BB_DIR ?= $(dir $(OUT))busybox-$(ARCH)` (`busybox.mk:32`).
- The package system is userland only (constitution Invariant 8, `prompts/MASTER ...md:1976-1978`; §47-48 at `:1557-1614`
  asks for declarative, reproducible recipes).

---

## 2. Current project state and direction

- **Version**: latest tag `v1.0.9`, which is main (`git describe --tags main` = `v1.0.9`). Release notes are in
  `docs/releases/v1.0.4.md` … `v1.0.9.md`. `README.md:129-141` describes 1.0 as done.
- **Roadmaps**:
  - `docs/roadmap-1.2.md` is the newest. It was written 2026-10-20 at v1.0.7; the owner adopted direction **B (storage
    depth)** on 2026-10-20 (`roadmap-1.2.md:3-12`). `AGENTS.md:9-12` confirms that 1.0 and 1.1 are complete.
  - B milestones: B1 snapshots [x] (`:188`), B2 fsck/scrub [ ] (`:210`, the branch being worked), B3 mirror [ ] (`:226`),
    B4 boot environments [x] (`:241`), B5 encrypted root [ ] (`:265`).
  - The "Always" first-unit lines (`:25-38`) include **BUILD-1, "the kernel builds without the stack protector"** (`:37`).
- **Deferred direction A, self-hosting** (`roadmap-1.2.md:42-140`), which interacts with Nova:
  - A1: TinyCC on the target, against **musl**, with clang+lld as a static musl binary under the Linux personality to be
    evaluated (`:54-69`).
  - A3: `pkg build NAME` on the target, with recipes that pin URL and SHA-256 and declare build and test commands
    (`:86-105`).
  - None of A is scheduled (`:44`).
- **Deferred direction C** (`:294-390`): C2 KASLR (`:322-330`) would affect module relocations and kernel-symbol assumptions
  (inferred).
- **Backlog lines relevant to Nova** (`docs/backlog.md`):
  - BUILD-1, kernel without stack protector (`:81`); BUILD-3, coverage and LTO configurations (`:83`); BUILD-4, KASAN-like
    kernel sanitiser (`:84`).
  - **BUILD-6**, "Native programs are static: no shared native libc and no native `PT_INTERP` (deferred by the owner past
    1.1)" (`:86`).
  - **SEC-5**, "Native programs and static BusyBox are `ET_EXEC`: ASLR does not move their code (static-PIE)" (`:255`).
  - PKG-6, library versions and provides/requires (`:228`); PKG-7, build sandboxing, SBOMs, multi-architecture packages
    (`:229`).
  - The backlog has **no libc section** (subsystem table at `:34-55`). The libc gaps live in `docs/libc/invariants.md:308-322`.
- **Untracked `Todo.md`** (2026-10-06):
  - Plans a float `libm` from musl `src/math/` added to the native libc as `libc/src/math/*.c` plus `libc/include/math.h`
    (`Todo.md:22-40`).
  - It notes the C++ runtime as "the big one" for ClayWind/ThorVG (`Todo.md:53-59`).
  - Another external project is already pressing on the same gaps in the native libc that Nova will meet.

---

## 3. Build toolchain

### 3.1 Host requirements

- **One LLVM toolchain for every target**: `CC := $(LLVM_PREFIX)clang`, `LD := ld.lld`, `lld-link`, `llvm-objcopy`,
  `llvm-ar`, `llvm-nm`, `llvm-objdump` (`build/toolchain.mk:3-19`). No GCC anywhere, and no per-target binutils.
- `scripts/check-tools.sh:21`: `need "$cc" "install clang (LLVM >= 15)"`; it also needs lld, llvm, mtools,
  `qemu-system-x86_64` and python3 (`:22-29`).
- **Primary host** is an ARM64 Linux VM. `scripts/setup-dev-linux.sh` installs `clang lld llvm libclang-rt-dev make git mtools
  musl-tools qemu-system-x86 ovmf qemu-system-arm qemu-efi-aarch64 python3` (`docs/development.md:21-33`).
- **Secondary host** is macOS + Homebrew (`docs/development.md:35-57`):
  - GNU make 4 is required (Apple ships 3.81).
  - Apple clang 21 targets both triples, but `ld.lld` comes from swiftly or Homebrew llvm. On this machine `ld.lld` resolves
    to `/Users/kutty/.swiftly/bin/ld.lld` and `clang` is Apple clang 21.0.0.
- GNU make 4.3 or newer is enforced (`build/config.mk:54-58`, needed for grouped targets).
- **CI image** (`ci/Dockerfile:17-23`, `ci/README.md:3-8`): Debian trixie pinned by digest, with `clang lld llvm
  libclang-rt-dev make mtools python3 musl-tools`, QEMU (10, per `docs/development.md:507-509`), ovmf and qemu-efi-aarch64.
  The analyzer baseline names Debian clang 19.1.7.
- **Python 3** is a hard build dependency (`PYTHON ?= python3`, `build/toolchain.mk:20`). It drives modsign, pkgbuild,
  mkbootarchive, mkmanifest, the boot harness and the analysis gate. There is no .NET anywhere in the build or CI today (grep
  of `docs/` and `prompts/` for `.NET`/`C#` finds only `plan.md:767` and constitution `:1659-1661`).

### 3.2 Target triples and per-arch flags

| | x86-64 | AArch64 |
|---|---|---|
| Kernel, module **and user** triple | `x86_64-unknown-none-elf` (`build/arch/x86_64.mk:3`) | `aarch64-unknown-none-elf` (`build/arch/aarch64.mk:3`) |
| Loader triple | `x86_64-unknown-windows` (`:4`) | `aarch64-unknown-windows` (`:4`) |
| Kernel/module arch flags | `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only -fno-pic -fno-pie -DARCH_X86_64=1` (`x86_64.mk:9-11`) | `-march=armv8-a -mcmodel=small -mgeneral-regs-only -mno-outline-atomics -mno-omit-leaf-frame-pointer -ffixed-x18 -fno-pic -fno-pie -DARCH_AARCH64=1` (`aarch64.mk:18-21`) |
| QEMU | `qemu-system-x86_64` q35 (`x86_64.mk:23`) | `qemu-system-aarch64` virt, cortex-a72 (`aarch64.mk:33`; `docs/development.md:283`) |

**User programs use the same `-unknown-none-elf` triple as the kernel** (`libc/libc.mk:12-13`: `--target=$(KERNEL_TARGET)`).
They do not use a `-linux-` or `-cosmoos-` triple.

### 3.3 Exact userland CFLAGS and LDFLAGS

`USER_CFLAGS` (`libc/libc.mk:12-18`):

- `--target=$(KERNEL_TARGET)` plus `$(COMMON_CFLAGS)` plus `-fno-pic -fno-pie -Wno-missing-prototypes`;
- `-I$(ROOT)/libc/include -I$(ROOT)/kernel/include` (the second is for `uapi/cosmo/*.h`).

`COMMON_CFLAGS` (`build/toolchain.mk:34-61`):

```
-std=c11 -ffreestanding -nostdlib -nostdinc
-fno-stack-protector -fno-omit-frame-pointer -fno-strict-aliasing
-fno-asynchronous-unwind-tables -fno-unwind-tables
-ffile-prefix-map=$(ROOT)=/cosmo
-DCOSMO_BUILD_ID=... -DCOSMO_BUILD_TYPE=... -DCONFIG_SELFTEST=... -DCONFIG_LOCKDEP=... (+7 more CONFIG_* -D's, :39-49)
COMMON_WARNINGS: -Wall -Wextra -Werror -Wshadow -Wvla -Wundef -Wpointer-arith -Wwrite-strings
  -Wstrict-prototypes -Wmissing-prototypes -Wmissing-declarations -Wimplicit-fallthrough
  -Wcast-qual -Wformat=2 -Wdate-time                     (:27-31)
debug:   -O1 -g -DCONFIG_DEBUG=1      release: -O2 -g -DCONFIG_DEBUG=0   (:52-56)
-isystem $(clang -print-resource-dir)/include          (:60-61)
```

Points that matter for generated C:

- **`-Werror` with this warning set applies to generated code** (B4). VLAs are forbidden (`-Wvla`), as are casting away
  const (`-Wcast-qual`), shadowing and implicit fallthrough. `-Wmissing-prototypes` is relaxed for userland only (libc.mk:16).
- **`-ffreestanding -nostdinc`**: the only headers are `libc/include/*` and clang's resource headers (`stdint.h`,
  `stddef.h`, `stdbool.h`, `stdarg.h`; `stdatomic.h`, `stdalign.h` and `stdnoreturn.h` also come from the resource dir
  (inferred)).
- **`-fno-stack-protector`** applies to userland as well. libc provides no `__stack_chk_fail` (grep finds none in
  `libc/src`). Nova must not turn the stack protector on unless the runtime supplies the guard symbols (inferred).
- **No unwind tables**, and `user.ld` discards `.eh_frame` (`userland/user.ld:68-72`). Nova cannot rely on unwinding, so no
  C++-style exceptions (inferred).
- User code **may use FP/SIMD registers** (`libc/libc.mk:8-11`, `docs/development.md:163-168`). The kernel saves them per
  thread.
- On x86-64 the red zone is allowed in user code: there is no `-mno-red-zone` in USER_CFLAGS
  (`docs/development.md:164`).
- On AArch64 there is **no `-mno-outline-atomics`** for user code. The kernel has it (`aarch64.mk:19`); USER_CFLAGS does not
  (libc.mk:12-18). With a `none-elf` triple clang does not emit outline atomics by default (inferred, not verified by a
  build).

`USER_LDFLAGS` (`libc/libc.mk:20-23`):

```
-nostdlib -static --no-dynamic-linker -z max-page-size=0x1000 -z noexecstack -z separate-code --build-id=none --gc-sections
```

Link line (`userland/userland.mk:84-89`):

```
ld.lld $(USER_LDFLAGS) -T userland/user.ld -o $(OUT)/userland/<name>.elf  $(OUT)/libc/src/arch/$(ARCH)/crt0.o  <objs>  $(OUT)/libc/libc.a
```

**No compiler-rt builtins and no libgcc are linked into native programs.** Only musl-side programs get
`libclang_rt.builtins.a` (`ports/busybox/cc.in:14-16`, `busybox.mk:42`). Generated C that makes clang emit libcalls will
fail to link on the native path (inferred). Examples:

- `__int128` division (`__udivti3`/`__divti3`);
- `long double` arithmetic on AArch64, where `long double` is IEEE quad. The host check
  `clang --target=aarch64-unknown-none-elf -dM -E` gives `__SIZEOF_LONG_DOUBLE__ 16`, and
  `busybox.mk:54-58` notes that musl needs the quad builtins;
- 16-byte `__atomic` operations;
- some float↔int128 conversions.

### 3.4 Linker script (`userland/user.ld`)

- `ENTRY(_start)` (`:12`). The image is linked at **0x400000** (4 MiB, `:26`), which is `VM_USER_LO`
  (`kernel/include/kernel/vmm.h:151`).
- PHDRS (`:14-22`):
  - `text` PT_LOAD R+X, carrying the FILEHDR and PHDRS so that AT_PHDR works;
  - `rodata` R; `data` RW;
  - `note` PT_NOTE (`.note.cosmo`);
  - `tls` PT_TLS (`.tdata` + `.tbss`);
  - `PT_GNU_STACK` RW.
- Three W^X segments. The kernel refuses W+X segments and executable stacks (`kernel/process/elf.c:109-111, 125`).
- Discarded: `.eh_frame`, `.eh_frame_hdr`, `.comment`, `.note.GNU-stack` (`:68-72`).
- **No `.init_array`/`.fini_array` handling**: the script has no such output section, and there is no constructor runner in
  libc (grep of `libc/` for `init_array` or `constructor` finds nothing). Nova must not depend on C constructors or
  destructors. Static initialisation has to be explicit, from the generated `main` (inferred).

### 3.5 How a userland program gets into the image

1. `userland/userland.mk:10-12` lists programs in `USER_BIN_PROGRAMS` or `USER_SBIN_PROGRAMS`, each with
   `PROG_DIR_<name> := <dir>` (`:14-61`). Extra sources go in `PROG_EXTRA_SRCS_<name>` (`:36`).
2. Test-only programs go in `USER_TEST_PROGRAMS := thrtest cwdtest` (`:65-67`). They are built only when `SELFTEST=1`
   (`:69-72`).
3. Sources are `userland/$(PROG_DIR_x)/x.c` (`:77`). They compile through `compile_rules`, whose pattern is
   `$(OUT)/%.o: $(ROOT)/%.c` (`build/rules.mk:15-19`), so **a source must live under `$(ROOT)`**.
4. Archive entries: `bin/<name>` (renamed `cosmo-<name>` if BusyBox has that applet, `:95-101`), `sbin/<name>`, and for
   tests `tests/native/<name>` (`:112-115`).
5. `Makefile:81-99`: `BOOT_ARCHIVE_ENTRIES` (init, the user entries, musl, BusyBox, `sbin/pkg`, `repo/*`, Linux tests,
   modules, …) → `scripts/mkbootarchive.py` → `$(OUT)/boot.tar`.
6. Then the signed manifest (`Makefile:120-123`, `scripts/mkmanifest.py`) and `scripts/mkimage.sh` for the GPT/FAT32 image
   `$(OUT)/cosmoos.img` (`Makefile:125-127`).
7. On the target, the ramfs places `bin/`, `sbin/` and `etc/` at `/bin`, `/sbin` and `/etc`, and everything else under
   `/boot`. So `tests/native/x` becomes `/boot/tests/native/x` (`docs/development.md:177-184`).
8. The documented procedure for a new program is `docs/development.md:191-195`.

### 3.6 Port recipe format (usable by a Nova port as-is)

- `tools/pkgbuild.py:11-17`: keys `name`, `version`, `summary`, `prefix` (default `/usr`, `:63`), `depends` (repeatable),
  `program` (a destination, then C sources), `file` (destination, source, mode), `provides`, `requires`.
- Example `ports/hello/port:1-6`:

  ```
  name: hello
  version: 1.1
  summary: prints a greeting
  program: bin/hello src/hello.c
  file: share/doc/hello/README doc/README 0644
  ```

- `compile_program` (`tools/pkgbuild.py:180-194`):
  - Sources must be inside the port directory: absolute paths and `..` are refused (`:183-187`).
  - Each source is compiled as `cc $USER_CFLAGS -c`, then linked as `ld $USER_LDFLAGS -T user.ld crt0.o objs libc.a`.
  - `pkg.mk:56-60` passes exactly the native userland flags.
- **Only C sources are accepted. There is no hook for a pre-compile code generator.** The package is deterministic ustar plus
  an Ed25519 `COSMOSIG` trailer (`pkgbuild.py:18-21`).
- On the target the repository is at `/boot/repo`. `pkg install <name>` works offline (`docs/development.md:197-212`). The
  harness already types `pkg install hello && hello && pkg list` in every build (`:208-210`).

---

## 4. Userland ABI

### 4.1 libc: an in-house native library, small

- **Native libc** is `libc/`: about 3.3 kLoC in `src/*.c` and 1.3 kLoC of headers (`wc -l`). It is static only (`libc.a` plus
  `crt0.o`, `libc/libc.mk:5-6, 54-58`).
- Sources (`libc/libc.mk:28-48`): errno, tcb, auxv, tlsscan, thread, ctype, malloc, stdlib, conv, printf, stdio, unistd,
  dirent, process, signal, termios, socket, cosmo, string (compiled with `-fno-builtin`, `:25-26`).
- Headers (`libc/include/`):

  ```
  assert ctype dirent errno fcntl limits signal spawn stdio stdlib string termios time unistd
  arpa/inet netinet/in sys/{mman,mount,socket,stat,types,un,wait}
  cosmo/{auxv,hv,klog,procinfo,syscall,sysctl,tcb,thread}
  ```

- **Missing headers**: `math.h`, `pthread.h`, `setjmp.h`, `poll.h`/`sys/select.h`, `netdb.h`, `locale.h`, `wchar.h`,
  `stdatomic` beyond clang's.
- Documented gaps (`docs/libc/invariants.md:308-322`):
  - no `<math.h>`, locales or wide characters;
  - no `fork`, `exec*`, `system`, `popen`, `setjmp`, `getpwnam`;
  - no dynamic linking;
  - no `sockaddr_in` or `sockaddr_in6`.
  - `libc/README.md:17-21` adds "No `fork`", plus no `long double`, no `%a` and no libm.
- **stdio** (`libc/include/stdio.h:13-47`):
  - `fopen`, `fdopen`, `fclose`, `fflush`, `fread`, `fwrite`, `fgetc`, `fgets`, `ungetc`, `fputc`, `fputs`, `puts`,
    `fseek`, `ftell`, `rewind`, `fileno`;
  - the printf family (`printf`, `fprintf`, `dprintf`, `sprintf`, `snprintf` and the `v*` forms), `perror`, `remove`,
    `rename`. `BUFSIZ` is 1024.
  - Length modifiers `l`, `z`, `h`, `j` and `t` are parsed (`libc/src/printf.c:375-378`). `%f`, `%e` and `%g` are supported;
    `%a` is not (`libc/README.md:18-21`).
  - There is no `scanf` and no `strtod` (grep).
- **stdlib** (`libc/include/stdlib.h:6-25`):
  - `malloc`, `calloc`, `realloc`, `free`;
  - `exit`, `_exit`, `abort`, `atexit` (32 slots, `libc/src/stdlib.c:47`);
  - `getenv`, `setenv`, `unsetenv`;
  - `atoi` and the `strto[u]l[l]` family; `qsort`, `abs`, `labs`.
  - There is **no `aligned_alloc`/`posix_memalign`** (grep).
- **string** (`libc/include/string.h:4-28`): the mem* functions, `strlen`, `strnlen`, the cpy/cat/cmp family, `strchr`,
  `strrchr`, `strstr`, `strspn`, `strcspn`, `strpbrk`, `strtok_r`, `strdup`, `strndup`, `strlcpy`, `strlcat`, `strerror`
  (a `_Thread_local` buffer, `cosmo/thread.h:29`).
- **errno**: `int *__errno_location(void)` (`libc/include/errno.h:12`). It is per thread, in the TCB behind the thread pointer
  (`libc/README.md:14-17`; `docs/libc/invariants.md` L8).
  - There is one translation point: `__syscall_ret(r)`, which does `if (r<0){errno=-r;return -1;}`
    (`docs/libc/design.md:31-33`).
  - Invariant L3: every failure sets errno and returns the sentinel. The library never prints or exits on its own, except
    `abort` and `assert` (`docs/libc/invariants.md:27-33`).
  - `abort` writes `abort()` and calls `_exit(134)` (`docs/libc/design.md:256-259`).
- **Invariant L1** (`docs/libc/invariants.md:7-14`): programs reach the kernel only through the library. The raw `cosmo_*`
  wrappers are internal; only `init --selftest` uses them. There is no build rule enforcing this; the check is review.
  **A Nova runtime that calls `cosmo_*` directly would break L1 unless the runtime counts as library code** (inferred; this
  is a design question for the owner).

### 4.2 malloc: the allocator design

`libc/src/malloc.c:1-17` and `docs/libc/design.md:94-108`:

- Arenas are 64 KiB (`ARENA_SIZE`, `:25`), obtained with `mmap(NULL, size, PROT_READ|PROT_WRITE, MAP_ANONYMOUS, -1, 0)`
  (`:97-101`).
- Requests above 16 KiB (`BIG_THRESHOLD`, `:26`) get their own page-rounded mapping, flagged `BIG`, and are `munmap`ped on
  `free` (`:129-138`).
- Each block has a 16-byte header `{size_t size (low bits INUSE|BIG); size_t prev_size}` (`:35-38`).
- **Alignment is 16** (`:27`).
- Allocation is first-fit over one singly linked free list (`:140-159`). A split happens only if the remainder is at least
  32 + 16 bytes (`:147`).
- `free` coalesces both physical neighbours. Each arena ends in a zero-size in-use sentinel (`:71-76`).
- Requests above 2^40 fail with ENOMEM (`:124-127`). `calloc` checks for overflow (`:169-172`).
- **One global non-recursive `cosmo_mutex_t`** covers the whole allocator (`:51-59`): one CAS when uncontended.
- Invariant L5: no overlap or misalignment, and a double `free` calls `abort` (`docs/libc/invariants.md:44-54`). Gaps there:
  no randomised stress test and no debug guard bytes.
- Arenas are never returned to the kernel; only BIG blocks are unmapped (inferred from `free_nolock`/`new_arena`; arenas
  have no unmap path at `malloc.c:103-113`).
- Performance is "O(n) in free blocks per call" (`docs/libc/design.md:270`).
- **Doc/code conflict**: `docs/libc/design.md:108` says "No thread safety (none needed)", but the code is locked
  (`malloc.c:9-16`).
- Host test: `tests/host/test_libc.c` compiles `malloc.c` over a fake mmap under ASan/UBSan (`docs/development.md:101-103`).

### 4.3 crt0, startup and entry

x86-64 `_start` (`libc/src/arch/x86_64/crt0.S:13-21`):

- `xor %ebp,%ebp`; `rdi`=argc from `(%rsp)`; `rsi`=argv=`rsp+8`; `rdx`=envp=`rsp+16+8*argc`.
- `and $-16,%rsp`; `call __libc_start`. It never returns.

AArch64 `_start` (`libc/src/arch/aarch64/crt0.S:12-24`):

- `x29`/`x30` are zeroed; `x0`=argc, `x1`=argv, `x2`=envp.
- sp is 16-aligned; `bl __libc_start`.

Both emit the **`.note.cosmo` ABI note** (x86 `:26-30`, a64 `:28-32`): namesz 8, descsz 4, type 1, name `"CosmoOS"`,
desc = ABI version 1.

- **The note selects the native personality. A static ELF without it runs as a Linux program** (crt0 comments; the kernel
  sets `cosmo_note` at `kernel/process/elf.c:97-104`; `docs/development.md:233-237`).
- So a Nova program on the native path must link `crt0.o` or emit the same note.

Initial process state (`docs/kernel/syscall/api.md:481-491`):

- sp points at argc, argv…, NULL, envp…, NULL, then auxv (tag,value) pairs ending in AT_NULL, with the strings after.
- Every general register is zero, and the stack is 16-aligned at argc.
- Handles 0, 1 and 2 are the console for init. A spawned child has exactly the handles its parent mapped.
- The stack is 8 MiB below `0x00007FFFFFFF0000`, with a guard page. `USER_STACK_SIZE` is 8 MiB
  (`kernel/include/kernel/process.h:74-75`). ASLR moves the stack top and sp (`process.h:63-71`).

auxv tags (`kernel/include/uapi/cosmo/syscall.h:790-810`): `AT_NULL 0`, `AT_PHDR 3`, `AT_PHENT 4`, `AT_PHNUM 5`,
`AT_PAGESZ 6`, `AT_ENTRY 9`, `AT_RANDOM 25` (among others). The accessor is `cosmo_getauxval()`
(`libc/include/cosmo/auxv.h:28`).

`__libc_start(argc, argv, envp)` (`libc/src/stdlib.c:53-...`):

1. `__cosmo_auxv_init(envp)` comes first (`:71`).
2. `__cosmo_tcb_init()` installs the thread pointer and the TLS image (`:86`). On failure it writes one line to fd 2 with a
   raw syscall and exits 127 (`:87-100`).
3. Then stdio init, `main`, and `exit(main's return)` (`docs/libc/design.md:89-92`).

Notes:

- **The prototype libc calls is `int main(int argc, char **argv, char **envp)`** (`libc/src/stdlib.c:51`).
- `exit` runs the `atexit` handlers in reverse, flushes every FILE, then `_exit`. `_exit` and `abort` do not flush
  (invariant L6, `docs/libc/invariants.md:56-60`).

### 4.4 Syscall ABI

Source: `kernel/include/uapi/cosmo/syscall.h:1-12`, and `docs/kernel/syscall/api.md:7-31`.

- **x86-64**:
  - The instruction is `syscall`. The number goes in `rax`; arguments go in `rdi rsi rdx r10 r8 r9`; the result comes back
    in `rax`.
  - `rcx` and `r11` are clobbered. Every other register is preserved.
- **AArch64**:
  - The instruction is `svc #0`. The number goes in `x8`; arguments go in `x0..x5`; the result comes back in `x0`.
  - Every other register is preserved.
- **Errors**: a result in `[-4095,-1]` is `-errno`; anything else is success. The kernel never returns a kernel pointer
  (`api.md:18-20`).
- **Numbering is native and differs from Linux.** `SYS_exit 0`, `write 1`, `read 2` … `SYS_reboot 105`, `SYS_COUNT 106`
  (`uapi/cosmo/syscall.h:20-171`). The ABI is stable: "add, never renumber" (`:4-5`; `api.md:9-11`). Numbers and structures
  are identical on both architectures (`docs/libc/design.md:84-87`).
- Inline wrapper: `cosmo_syscall6` plus the `cosmo_syscall0..5` macros (`libc/include/cosmo/syscall.h:18-54`). Typed raw
  wrappers follow, e.g. `cosmo_exec` `:230`, `cosmo_aio_*` `:458-470`, `cosmo_timer_create` `:473`, `cosmo_thread_create`
  `:541`, `cosmo_futex_*` `:564-578`.
- Descriptors are **handles with rights**: `SYS_dup (h, target, rights)` (`syscall.h:61`), `dup_rights()`
  (`libc/include/unistd.h:49`), and `spawn_handle` mapping (`spawn.h:9-17`).
- The syscall filter is `SYS_syscall_filter` (`:100`, `unistd.h:42`).

### 4.5 ELF details

- The kernel loader accepts `ET_EXEC` and `ET_DYN` (`kernel/process/elf.c:52-55`). ET_DYN executables are rebased at
  `USER_PIE_BASE` 0x555500000000 plus random (`kernel/process/process.c:527-535`; `process.h:77`).
- `PT_INTERP` is supported: the interpreter is loaded at the mmap base (`process.c:541-605`).
- **Native programs are static `ET_EXEC` only.** The base is fixed at 0x400000, and native code gets no ASLR (SEC-5,
  `backlog.md:255`). There is no shared native libc (BUILD-6, `:86`).
- Dynamic linking exists **only for musl programs** under the Linux personality: `/lib/ld-musl-$(ARCH).so.1`
  (`busybox.mk:36-41, 172-174`; `tests/dyn/dyn.mk:1-25`).
- Static-PIE native programs are not built today. Kernel support for ET_DYN without an interpreter looks present, but libc
  has no self-relocation (inferred).
- W^X is enforced per segment, and an executable `PT_GNU_STACK` is refused (`elf.c:109-125`).

### 4.6 TLS

- `__thread`/`_Thread_local` work in native programs: `user.ld` emits PT_TLS (`userland/user.ld:41-58`, which notes that
  without it "`ld.lld` refuses an STT_TLS symbol").
- libc finds PT_TLS through AT_PHDR (`tlsscan.c`). It copies `.tdata` and zeroes `.tbss` per thread.
- The TCB is 128 bytes (`libc/include/cosmo/tcb.h:47`):
  - AArch64 uses variant I with a 16-byte ABI head at tp (`:70-71`);
  - x86-64 uses tp offset 0 (`:73-74`).
- The thread pointer is set with `SYS_set_tls` (FS base / TPIDR_EL0; `uapi/cosmo/syscall.h:135, 194`).
- **Any thread that calls libc must have libc's TCB prefix** (`cosmo/thread.h:38-55`). A raw `SYS_thread_create` with
  `tls=0` faults on the first errno access.
- The TLS model is local-exec, because the code is static and non-PIC (inferred).

### 4.7 Signals

- Header: `libc/include/signal.h:1-96`.
  - `sigaction`, `signal`, `sigprocmask`, `sigpending`, `raise`, `kill`, and the sigset ops (`:83-94`);
  - `SA_SIGINFO`, `SA_RESTART`, `SA_NODEFER`, `SA_RESETHAND` (`:49-55`);
  - `sigset_t` is `unsigned long` (`:43`).
- libc owns the restorer `__cosmo_sigreturn` (`libc/src/signal.c:23-35`).
- "Not here, because the kernel does not have them: real-time signals, queued siginfo, **sigaltstack and SA_ONSTACK**,
  sigsuspend, sigwait" (`signal.h:11-12`). **Nova cannot catch stack overflow on an alternate stack.** A guard-page hit kills
  the process (inferred).
- Per-thread signals: `cosmo_thread_kill` (`cosmo/thread.h:106`; `SYS_thread_kill` `syscall.h:154`).

### 4.8 The second user ABI: Linux personality with musl (relevant alternative)

- `compat/linux/` implements a Linux syscall personality with tables for both architectures (`compat/linux/nr_x86_64.h`,
  `nr_aarch64.h`; `docs/compat/linux/design.md:285-290`, "the AArch64 table").
- **musl 1.2.5 is built from source** for `$(ARCH)-linux-musl` with the project clang plus compiler-rt builtins 19.1.7
  (`ports/busybox/busybox.mk:10-18, 33-44, 61-90`).
  - The compiler wrapper is `$(BB_DIR)/cc` (`ports/busybox/cc.in:1-18`): `--target=<arch>-linux-musl -nostdinc -isystem
    sysroot/include -fuse-ld=lld -static -Wl,--image-base=0x400000 crt1.o crti.o ... -lc libclang_rt.builtins.a crtn.o`.
  - A dynamic variant `cc-dyn` also exists (`busybox.mk:44`).
- Base-system programs already use this path: BusyBox is `/bin/sh` (`userland/userland.mk:95-97`); `ssl_client` and
  `cosmo-fetch` (which `pkg` and `cosmo-upgrade` run) are static musl (`ports/mbedtls/mbedtls.mk:1-9`); so are dropbear
  and openssh.
- Musl gives full POSIX: pthreads, libm, `setjmp`, `getaddrinfo`, `poll`/`epoll` (`tests/linux/epoll_musl.c` exists). Linux
  gaps are tracked as LX-1…LX-23 (`backlog.md:136-160`).
- The development docs say the Linux personality is "x86-64 only" (`docs/development.md:246-249`); this is stale, see §11.

---

## 5. User-space APIs relevant to a Nova std lib

| Area | Native libc API | Kernel-only (raw `cosmo_*`, no public libc wrapper) |
|---|---|---|
| **Files** | `open` (`fcntl.h:16`, flags O_RDONLY…O_NONBLOCK `:5-15`), `read/write/close/lseek` (`unistd.h:14-17`), `stat/lstat/fstat/mkdir/mkfifo` (`sys/stat.h:24-28`), `unlink/rmdir/symlink/readlink/access/fsync/sync` (`unistd.h:54-61`), `opendir/readdir/closedir` (`dirent.h:18-20`), `rename/remove` (`stdio.h:46-47`), `chdir/getcwd` (`unistd.h:30-31`), `pipe/dup/dup2/dup_rights/isatty` (`:44-51`) | `SYS_getdents 19`, `SYS_mknod 100`, `SYS_mount/umount 21-22`, `SYS_statx`-style not present |
| **Sockets** | `socket/bind/listen/accept/connect/sendto/recvfrom/send/recv/shutdown/getsockname/getsockopt/socketpair` (`sys/socket.h:30-43`); AF_UNIX/INET/INET6, SOCK_STREAM/DGRAM/NONBLOCK (`:5-20`); `inet_pton/inet_ntop` (`arpa/inet.h:6`); `htons...` (`netinet/in.h:5-11`). **Native address is `struct sockaddr {family, port (host order), flowinfo, addr[16], scope}`** (`sys/socket.h:23-29` = `struct cosmo_sockaddr`, `uapi/cosmo/syscall.h:620-626`); no `sockaddr_in` (`invariants.md:321-322`) | `SYS_sendmsg/recvmsg 97-98` (handle passing over unix sockets), `SYS_ioready 58`, `SYS_setnonblock 59`; **no `poll`/`select`/`epoll` in native libc**, no `getaddrinfo` (grep) |
| **Processes** | **no fork/exec**: `spawnve/spawnvp` (+ `_pgrp`, `_fg`, `_in`, `_domain`, `_mountns`, `_utsns`, `_session`, `_as` variants) (`spawn.h:17-69`), `waitpid/wait` (`sys/wait.h:22-23`), `kill` (`signal.h:94`), `getpid/getppid`, pgid/session/tty calls (`unistd.h:18-29`), uid/gid calls (`:62-73`), `getenv/setenv` | `SYS_exec 103` exists (`syscall.h:168`, `cosmo_exec` `cosmo/syscall.h:230`) but libc exports no `exec*` (`invariants.md:314`) |
| **Time** | `clock_gettime(CLOCK_MONOTONIC|CLOCK_REALTIME)` (`time.h:9-12`; impl `libc/src/unistd.c:180-188`), `nanosleep`, `sleep`, `usleep` (`unistd.h:52-53`). No `time()`, `gettimeofday`, `localtime` | `SYS_timer_create 101` (timerfd-like handle), `SYS_sleep_ns 5`, `SYS_clock_ns 6` |
| **Memory mapping** | `mmap/munmap/mprotect/msync` (`sys/mman.h:26-37`): len must be a page multiple (EINVAL, not rounded), anonymous may be `MAP_PRIVATE` but not `MAP_SHARED` (`:12-25`), `MAP_FIXED`, `MAP_FIXED_NOREPLACE` | hint-less mmap searches up from `USER_MMAP_BASE` 0x100000000000 + ASLR (`process.h:76`) |
| **Threads** | **No pthreads.** `cosmo/thread.h`: `cosmo_thread_start(t, fn, arg, stack_size)`, `cosmo_thread_join`, `cosmo_thread_finish`, `cosmo_thread_id`, `cosmo_thread_kill` (`:83-106`); `cosmo_mutex_t` lock/unlock/trylock (`:113-121`, futex, non-recursive); `cosmo_cond_t` wait/timedwait/signal/broadcast (`:146-211`, requeue-based). Functions return `-errno`, not errno (`:57-59`) | `SYS_futex_wait/wake/requeue 83/84/94`, `SYS_thread_create 85` (`struct cosmo_thread {entry, arg, stack_top, tls, clear_tid, flags}` `syscall.h:190-198`), `SYS_set_tls 87` |
| **Async I/O** | none | `SYS_aio_create/submit/wait 60-62` (ring of `cosmo_sqe`/`cosmo_cqe`, 1..1024 entries) |
| **Misc** | `cosmo_getauxval`, `sysctl_get`, `klog_read`, `procinfo` (`cosmo/*.h`), termios (4 flags; `invariants.md:317-320`) | `SYS_getrandom`: not in the native list (inferred from `syscall.h:20-171`; entropy for user code via `AT_RANDOM` only) |

The thread-safety contract is invariant L8 (`docs/libc/invariants.md:72`; `cosmo/thread.h:11-36`):

- the allocator, stdio (one whole `printf` is a single critical section), errno (per thread), the environment and the
  `atexit` table are all safe to use from several threads;
- `setenv` leaks deliberately so that pointers returned by `getenv` stay valid (`docs/libc/design.md:47-55`).

---

## 6. Testing and CI

### 6.1 Host unit tests and sanitizers

- `make host-test` (`tests/host/host.mk`) compiles real tree sources with the host `clang` and **no `--target`**
  (`HOST_CC ?= $(CC)`, `:5`). Binaries run under `-fsanitize=address,undefined -fno-sanitize-recover=undefined`
  (`:10, 19`).
- There are about 31 C test binaries (`:56`), including `test_libc`, which pulls in libc's `printf.c`, `malloc.c` and
  `conv.c` with renamed symbols (`docs/development.md:101-103`).
- Python host tests also run there (`host.mk:233-240`, e.g. `test_pkgbuild_libs.py`, `test_analysis_gate.py`). **This is the
  precedent for testing a host-side code generator.**
- `make host-test-quiesce-tsan` is a TSan model (`.github/workflows/ci.yml:198-199`).
- `make fuzz`: ASan/UBSan fuzz targets in `tests/fuzz/` (`docs/development.md:81`). It runs on the x86-64 CI job only
  (`ci.yml:201-204`).
- `make analyze`: the clang static analyzer against a reviewed baseline (`Makefile:518-527`). **`ANALYSIS_REPORTS` covers
  kernel, loader, modules and pkg, but not libc or userland programs** (`Makefile:518`), although `rules.mk:21-25` defines
  `.analyzed` rules for every compile.

### 6.2 QEMU integration harness

- `make test` (`Makefile:198-200`) runs `tests/boot/run_boot_test.py --timeout $(BOOT_TIMEOUT) --image ... --log ...`.
- The verdict comes from two independent signals (`run_boot_test.py:5-16`):
  1. the kernel's `isa-debug-exit` value (QEMU exits `(v<<1)|1`; 0x10 means success, 0x11 failure;
     `docs/development.md:427-462`);
  2. regex markers in the serial log: `REQUIRED_MARKERS` (`:276`) must all appear, and `FORBIDDEN_MARKERS` (`:597`) must
     not.
- A timeout, a panic or a missing marker fails the run. The default `--timeout` is 180 s (`:924`).
- There is a scripted console session (`tests/boot/shelltest.py`) and a network harness (`nettest.py`)
  (`docs/development.md:75`).
- The in-guest script is `userland/etc/rc.test`, archived only when `SELFTEST=1` (`userland/userland.mk:112-115`). It is
  straight-line shell where each check is `cmd || FAILS=1`, and it ends with `SHTEST: PASS`/`FAIL n`
  (`rc.test:1-6, 202`). The harness requires `^SHTEST: PASS` (`run_boot_test.py:465, 1258-1259`).
- **Precedent for a native test binary**: `userland/tests/thrtest.c` is listed in `USER_TEST_PROGRAMS`
  (`userland.mk:65-67`) and archived as `tests/native/thrtest`.
  - `rc.test:160` runs it as `/boot/tests/native/thrtest || FAILS=1`.
  - It prints `THREADTEST: PASS` (`thrtest.c:1630`).
  - The harness requires `^THREADTEST: PASS$` (`THREAD_MARKERS`, `run_boot_test.py:537-539`).
  - The libc test record is `docs/libc/testing.md:10-11, 13, 40`.
- Two more harness checks exist: user self-test sections (`USERTEST_SECTIONS`, `run_boot_test.py:76-79`) and package markers
  (`PKGTEST_MARKERS`, `docs/development.md:208-209`).
- **How to add a Nova test binary to the QEMU run** (derived from the precedent above):
  1. put the C under `$(ROOT)/...`;
  2. add it to `USER_TEST_PROGRAMS` with a `PROG_DIR_<name>`;
  3. add a line `/boot/tests/native/<name> || FAILS=1` to `rc.test`;
  4. add a `^NOVATEST: PASS$` marker list in `run_boot_test.py`;
  5. pass the validation matrix on both architectures.

  Alternatively, ship a port: `ports/<name>/port`, then `pkg install <name> && <name>` in `rc.test` plus a marker.

### 6.3 CI (`.github/workflows/ci.yml`, 895 lines)

- The runner is GitHub `ubuntu-24.04` x86-64. Jobs run in the project image `ghcr.io/vkuttyp/cosmoos-ci` (Debian trixie)
  (`ci.yml:1-23, 42-61`).
- **build-and-boot**, a matrix over `arch: [x86_64, aarch64]` (`:74-76`) with `timeout-minutes: 70` (`:90`). Steps in
  order:
  1. `check-tools`;
  2. debug `all`;
  3. `analyze`;
  4. debug `test`;
  5. `test-gic`, `test-guard`, `test-smp2`, `test-chaos`, `test-harness-retry`;
  6. release `all test` and the release-version check;
  7. `host-test`, `host-test-fetch`, `host-test-quiesce-tsan`;
  8. `fuzz` (x86_64 only);
  9. `reproducible`;
  10. `test-crash`, `test-wxn`;
  11. artifact upload (`:120-251`).
- Other jobs (`ci.yml` job keys at lines 264-877): `entropy`, `install`, `upgrade`, `security`, `net-login`, `pkg-net`,
  `busybox`, `release-set`, `platforms`, `docs`, `guide`, `guide-macos`, `litmus`.
- AArch64 runs under TCG on the x86 runner. "CI does not exercise an ARM64 host" (`docs/development.md:529-534`).
- The aarch64 job has a median of about 51.7 min (`ci.yml:85-89`), so **CI time budget is tight**: anything Nova adds to
  `all`/`test` lengthens a job already near its limit (inferred).

---

## 7. Kernel module build side (for Profile B later)

- `build/module.mk:14-15`:
  - `MODULE_CFLAGS := $(KERNEL_CFLAGS) -DCOSMO_MODULE_BUILD=1`;
  - `MODULE_LDFLAGS := -r --no-dynamic-linker -z noexecstack --build-id=none`.
- `KERNEL_CFLAGS` is `--target=<arch>-unknown-none-elf $(COMMON_CFLAGS) -Wframe-larger-than=2048 $(KERNEL_ARCH_CFLAGS)
  -I kernel/include -I drivers/include -I boot/protocol` (`build/toolchain.mk:66-74`).
  - x86-64 adds `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only`.
  - AArch64 adds `-mcmodel=small -mgeneral-regs-only -mno-outline-atomics -ffixed-x18 -mno-omit-leaf-frame-pointer`.
  - Both are `-fno-pic -fno-pie` (§3.2).
  - **No FP/SIMD** (`scripts/check-fpregs.sh`, build invariant B8). **Frames are limited to 2048 bytes** under `-Werror`.
- Per-module rule (`build/module.mk:67-80`):
  1. compile with MODULE_CFLAGS;
  2. `ld.lld -r -o X.ko.unsigned objs`;
  3. `scripts/modsign.py sign --key $(MODSIGN_KEY)`;
  4. `scripts/check-module-elf.py X.ko $(ARCH)`.
- Registration: `MODULES` list (`:27`), `MODULE_<name>_SRCS` (`:29-45`), and archive entries `modules/<n>.ko` (loaded at boot
  in order) or `tests/<n>.ko` (self-test fixtures) (`:48-65`). Procedure: `docs/development.md:121-145`.
- `check-module-elf.py:2-10` checks: ET_REL, little-endian ELF64 for the arch (e_machine 62 or 183), no W+X section, a
  `.cosmo.module` section of exactly **240 bytes**, and a `COSMOSIG` trailer.
- **Signing** (`scripts/modsign.py:2-18`):
  - Ed25519, pure Python (RFC 8032).
  - The trailer is `sig[64] key_id[8] version:u32 algo:u32 magic "COSMOSIG"` appended to the ELF and covering every earlier
    byte (format in `kernel/include/kernel/modsig.h`).
  - Keys: `SIGNING=dev` creates a per-machine key in `~/.config/cosmoos/keys` through `scripts/devkey.sh`; `release` needs
    `MODSIGN_KEY` and `KEYRING_PUBS` explicitly (`build/config.mk:42-77`).
  - `MODULE_SIG_ENFORCE ?= 1` (`:36-40`). The kernel's ring is generated into `$(OUT)/gen/keyring_builtin.c`
    (`docs/development.md:137-140`).
- Accepted relocations:
  - x86-64 (`kernel/arch/x86_64/modreloc.c`): `R_X86_64_64`, `PC32`, `PLT32`, `32`, `32S`, `NONE`.
  - AArch64 (`kernel/arch/aarch64/modreloc.c`, e.g. `:167`): `ABS64/32/16`, `PREL64/32/16`, `CALL26`, `JUMP26`,
    `CONDBR19`, `TSTBR14`, `LD_PREL_LO19`, `ADR_PREL_LO21`, `ADR_PREL_PG_HI21`, `ADD_ABS_LO12_NC`,
    `LDST{8,16,32,64,128}_ABS_LO12_NC`, `NONE`.
  - Out of range gives `-ERANGE` (`docs/kernel/module/api.md:173-174`).
  - Generated C must not produce GOT or TLS relocations (inferred from that list).
- ABI version: `COSMO_MODULE_ABI_VERSION 11u` (`kernel/include/kernel/module.h:23`, with a bump history in the comment).
  Magic is `"COSMOMOD"` (`:24`). The declaration macro is `COSMO_MODULE(name, version, init, shutdown, deps, caps)`, placed
  in `.cosmo.module` (`:60-64`).
- Exports: `EXPORT_SYMBOL` gives about 252 exported symbols (`docs/kernel/module/api.md:59-68, 134-135`).
- **Minimal module** (`modules/hello/hello.c:1-24`):

  ```c
  #include <kernel/log.h>
  #include <kernel/module.h>
  static unsigned hello_loads;
  static int hello_init(void) { hello_loads++; kinfo("hello: module init (ABI v%u, load %u)", COSMO_MODULE_ABI_VERSION, hello_loads); return 0; }
  static void hello_shutdown(void) { kinfo("hello: module shutdown"); }
  COSMO_MODULE("hello", "1.0", hello_init, hello_shutdown, "", MODULE_CAP_NONE);
  ```

- Test fixtures: `tests/modules/cosmotest{,_dep,_fail}.c` are built as modules and archived under `tests/`
  (`module.mk:43-45, 63-65`).

---

## 8. Precedents in tools/, compat/ and scripts/ for an external compiler or generator

- **Host code generators wired into Make**:
  - `scripts/gen-keyring.py` writes `$(OUT)/gen/keyring_builtin.c`, which the kernel compiles (`kernel/kernel.mk:207-210`,
    with a `GEN` log line). **This is the closest precedent for "a host tool emits C under `$(OUT)` that the cross toolchain
    then compiles."**
  - `tools/mkfont.py` is a font generator.
  - `scripts/gen-compile-commands.py` writes `compile_commands.json`.
- **A host tool that drives the cross compiler**: `tools/pkgbuild.py` calls `--cc`, `--cflags`, `--ld`, `--ldflags`,
  `--ldscript`, `--crt0` and `--libc` as given by Make (`pkg/pkg.mk:56-60`). A `novac` invocation could follow the same
  argument contract (inferred).
- **Upstream toolchain built from pinned source**: compiler-rt and musl in `ports/busybox/busybox.mk:54-90`, fetched by
  `scripts/fetch-pinned.py` (SHA-256 plus mirror, `:61-66`). Build trees sit beside `$(OUT)`, are shared across build
  variants (`:28-32`), and are cached in CI (`ci.yml:114`).
- **Compiler wrapper scripts** generated from templates: `ports/busybox/cc.in` and `cc-dyn.in`.
- **Code shared between host and target**: `tools/fdt/*.c` is compiled into the target `vmctl`
  (`userland/userland.mk:36`) and into host tests (`test_fdt`).
- **Per-file override hook**: `EXTRA_CFLAGS` is appended to every compile (`build/rules.mk:19, 24, 33`).
- `compat/` is a foreign *ABI* personality, not a toolchain (`compat/README.md:3`). It is not a precedent for a compiler.
- There is **no precedent for a non-Python host runtime (.NET) in the build**. CI's image has no `dotnet` (`ci/Dockerfile:22-23`).

---

## Implications for Nova (Profile A userland and build integration)

1. **Target triple and flags are fixed and simple.**
   - Generated C is compiled with `clang --target={x86_64,aarch64}-unknown-none-elf` and the USER_CFLAGS of §3.3, then linked
     static at 0x400000 through `user.ld` with `crt0.o` and `libc.a`.
   - Nova's backend should emit a target-layout table per triple: LP64 on both, `char` signed on x86-64 and **unsigned on
     AArch64**, `long double` 16 bytes on both, max alignment 16. It should never infer layout from .NET (brief §7).
   - Generated C must be `-std=c11`, warning-clean under the §3.3 set, free of VLAs, and free of libcalls that compiler-rt
     would provide (`__int128` division, quad `long double`, 16-byte atomics). It must not rely on constructors, unwinding,
     `setjmp` or stack-protector symbols.
2. **The native libc is a thin, POSIX-shaped C library with notable gaps.** Nova's std lib binds to:
   - files: open/read/write/stat/dirent;
   - native-shaped sockets;
   - `spawn*` + `waitpid` in place of fork/exec;
   - `clock_gettime` (MONOTONIC and REALTIME), `mmap`, and `cosmo_thread_*`/`cosmo_mutex_*`/`cosmo_cond_*` in place of
     pthreads.

   Absent: math, poll/select, DNS, `aligned_alloc`, `exec`, `setjmp`, alternate signal stacks. For each one, Nova either
   implements it in its runtime over raw syscalls (which strains invariant L1), proposes a libc addition (an owner-chosen
   unit), or uses the musl/Linux path. **This choice is the main Profile A design decision** (inferred).
3. **"Approved user-space allocator" means libc `malloc`**:
   - 16-byte alignment, one global lock, first-fit, O(n) free list, arenas never returned;
   - with no `aligned_alloc`, over-aligned types need runtime padding (inferred);
   - a Nova region/arena allocator can sit on `mmap(MAP_ANONYMOUS)` directly, through the public `mmap`, which does not
     violate L1.

   Allocation failure surfaces as `NULL`+`ENOMEM`, so Nova's fallible allocation maps cleanly.
4. **Threads**: `cosmo_thread_start` gives each thread a libc TCB. Nova must create threads only that way, never with a raw
   `SYS_thread_create` (`cosmo/thread.h:38-55`). `__thread` works.
5. **Entry**: generated `main(int, char**, char**)` is called by `__libc_start`; `exit` flushes stdio. Nova's deterministic
   cleanup must finish before `exit`/`_exit`, and `atexit` holds at most 32 entries.
6. **Personality trap**: a binary without `.note.cosmo` silently runs as a Linux program. Nova's link step must always
   include `crt0.o`. A CI check (e.g. `llvm-readelf -n` showing the note) is a cheap guard (inferred).
7. **Two viable Profile A back-ends**:
   - (A-native) native libc, which is what the brief asks for ("CosmoOS libc integration"), with a poor API surface;
   - (A-musl) the musl sysroot and `cc` wrapper that the BusyBox port already builds. This gives full POSIX and compiler-rt.
     It runs on both architectures, but under the *Linux* personality, which Invariant 7 separates from the native ABI.

   The base system already ships musl programs (`cosmo-fetch`, `ssl_client`), so A-musl is not foreign to the project, but
   it is not "native".
8. **Build integration must not disturb the existing workflow** (brief §9, `AGENTS.md:14-22`).
   - Generated C goes to `$(OUT)/nova/...` (B5) and must be byte-reproducible (B6). The Nova compiler's own version and
     inputs should be part of the stamp, like `SIGNING_STAMP` (`build/config.mk:78-84`) (inferred).
   - `compile_rules` assumes sources live under `$(ROOT)` (`build/rules.mk:16`), so `$(OUT)`-resident generated C needs its
     own pattern rule, as `pkg/pkg.mk:18-21` has (inferred).
   - .NET is not in CI's image, so CI integration needs either a Dockerfile change or a checked-in generated C (inferred).
9. **The analyzer gate** pins exact clang versions (Apple 21.0.0 and Debian 19.1.7). Profile B code entering
   `ANALYSIS_REPORTS` would face this gate; Profile A userland is outside it today (`Makefile:518`).

---

## Minimal path to C1 (Hello World in QEMU on both arches)

Option 1 is ordered least invasive first; option 2 is the eventual in-tree form. Both use only existing mechanisms.

### Option 1: out of tree, no CosmoOS edits until C1 is proven (recommended first; inferred)

1. **Nova** turns `hello.nova` into `hello.c` plus `nova_rt.c`/`nova_rt.h` (the runtime) in a Nova-owned out dir.
2. **Build the CosmoOS prerequisites** once per arch with the existing targets: `gmake ARCH=<arch> libc`, which produces
   `out/<arch>-debug/libc/libc.a` and `out/<arch>-debug/libc/src/arch/<arch>/crt0.o` (`docs/development.md:66`).
3. **Compile** with the exact flags:

   ```
   clang --target=<arch>-unknown-none-elf -std=c11 -ffreestanding -nostdlib -nostdinc -fno-stack-protector
     -fno-omit-frame-pointer -fno-strict-aliasing -fno-asynchronous-unwind-tables -fno-unwind-tables
     -O1 -g -DCONFIG_DEBUG=1 <COMMON_WARNINGS> -fno-pic -fno-pie -Wno-missing-prototypes
     -isystem $(clang -print-resource-dir)/include -I ~/dev/CosmoOS/libc/include -I ~/dev/CosmoOS/kernel/include
     -c hello.c nova_rt.c
   ```

   The CONFIG_* `-D`s from `toolchain.mk:39-49` are only needed if the code includes headers that test them (inferred).
   The most faithful option is to have CosmoOS's Make print USER_CFLAGS, or to copy them from `compile_commands.json` for a
   userland file (inferred).
4. **Link**:

   ```
   ld.lld -nostdlib -static --no-dynamic-linker -z max-page-size=0x1000 -z noexecstack -z separate-code --build-id=none
     --gc-sections -T ~/dev/CosmoOS/userland/user.ld -o nova-hello.elf crt0.o hello.o nova_rt.o libc.a
   ```

5. **Check**: `llvm-readelf -l -n nova-hello.elf` shows three W^X PT_LOAD segments at 0x400000, a PT_NOTE carrying
   "CosmoOS" type 1, and a non-executable PT_GNU_STACK.
6. **Run in QEMU**: the image is built only by CosmoOS Make, and the boot archive is generated from the entry lists
   (`Makefile:81-99`). Getting an external ELF in without editing the tree means a local, uncommitted experiment, which
   AGENTS rules keep out of PRs. The documented manual route for foreign binaries is "copy onto the cosmofs disk, boot, run
   from the shell" (`docs/development.md:229-231, 249-251`). The scratch disk is `QEMU_TESTDISK`, by default
   `out/<arch>-<build>/testdisk.img` (`docs/development.md:288`). **But in debug builds the kernel self-tests format it**
   (`docs/development.md:183-184`). For a manual C1 you need either a `BUILD=release` image or a separate disk through
   `QEMU_EXTRA` (inferred; needs verification).

### Option 2: in tree as a port (one directory, zero Makefile edits)

1. Add `ports/nova-hello/port`:

   ```
   name: nova-hello
   version: 0.1
   summary: Nova C1
   program: bin/nova-hello src/hello.c src/nova_rt.c
   ```

   Put the generated `src/*.c` and `*.h` inside the port. `pkgbuild.py:183-187` requires sources inside the port, so the
   **generated C is checked in**, which conflicts in spirit with B5's "generated output under `$(OUT)`". That is an open
   question for the owner.
2. `PORT_DIRS` is a wildcard (`pkg/pkg.mk:37`), so `make ports` and `all` build it for both arches with USER_CFLAGS and
   USER_LDFLAGS (`pkg.mk:56-60`). The package lands in `/boot/repo`.
3. **Automated proof**:
   - add to `userland/etc/rc.test` the line `pkg install nova-hello && nova-hello || FAILS=1`;
   - have the program print a fixed line such as `NOVATEST: PASS`;
   - add `r"^NOVATEST: PASS$"` to a marker list required in `tests/boot/run_boot_test.py`, modelled on `THREAD_MARKERS`
     (`:537-539`);
   - then run `make test` and `ARCH=aarch64 make test`, with the full matrix (`AGENTS.md:62-76`).
4. Alternative with Make edits (closer to brief §9):
   - add `nova/nova.mk` (included from `Makefile:57-66` alongside the others) with a rule
     `$(OUT)/nova/%.c: $(ROOT)/nova/%.nova` that runs `$(NOVAC)`;
   - add a pattern rule for `$(OUT)/nova/%.o` (as `pkg.mk:18-21`), and a link rule copied from `userland.mk:84-89`;
   - add an entry `tests/native/nova-hello=...` to the boot archive (as `userland.mk:113-114`), plus the rc.test and harness
     lines.

   This needs `dotnet` in `ci/Dockerfile` (which triggers an image rebuild, `ci/README.md:10-13`) and an owner-chosen unit.

### Files and targets a Nova-generated C program goes through

```
hello.nova --(novac, host .NET)--> $(OUT)/nova/hello.c        [B5, B6]
  --(clang USER_CFLAGS: build/toolchain.mk:27-61 + libc/libc.mk:12-18)--> .o
  --(ld.lld USER_LDFLAGS libc/libc.mk:20-23, -T userland/user.ld, crt0.o, libc.a)--> .elf (ET_EXEC @0x400000, .note.cosmo)
  --(userland.mk archive entry | pkgbuild.py .cpk + INDEX)--> scripts/mkbootarchive.py -> $(OUT)/boot.tar
  --> scripts/mkmanifest.py (signed manifest) -> scripts/mkimage.sh -> $(OUT)/cosmoos.img
  --> make test: tests/boot/run_boot_test.py (debug-exit + markers) under qemu-system-{x86_64,aarch64}
```

---

## Open questions and doc/code disagreements

### Disagreements found

1. **`CHAR_MIN`/`CHAR_MAX` are wrong on AArch64.**
   - `libc/include/limits.h:8-9` defines `CHAR_MAX SCHAR_MAX` and `CHAR_MIN SCHAR_MIN` unconditionally.
   - `clang --target=aarch64-unknown-none-elf` defines `__CHAR_UNSIGNED__ 1` (host preprocessor check), and USER_CFLAGS
     carry no `-fsigned-char` (grep).
   - So on AArch64 `char` is unsigned but `CHAR_MIN` = -128. This is a code defect, not only a doc one. Nova must not take
     `char` signedness from `limits.h`.
2. `docs/libc/design.md:108` says the allocator has "No thread safety (none needed)", but `libc/src/malloc.c:9-16, 51-59` takes
   a global mutex. `docs/libc/invariants.md` L8 and `cosmo/thread.h:11-16` agree with the code.
3. `docs/libc/invariants.md:310-311` says "`clock_gettime` is monotonic only" and "no ... wall clock", but `time.h:9` defines
   `CLOCK_REALTIME`, and `libc/src/unistd.c:185-186` implements it over `SYS_clock_ns` realtime
   (`uapi/cosmo/syscall.h:26`). `docs/libc/design.md:283` still lists "a wall clock ... when the kernel has one" as future
   work.
4. `docs/libc/design.md:78` shows crt0 storing `environ` (`mov %rdx, environ(%rip)`). The actual `crt0.S` (x86
   `:13-21`) does not; `__libc_start` does (`stdlib.c:44, 53+`).
5. `docs/development.md:246-249` says Linux test programs are "x86-64 only; on `ARCH=aarch64` the personality has no
   system-call table yet, `rc.test` prints `LINUXTEST: skipped`". In the code:
   - `compat/linux/nr_aarch64.h` exists, and `docs/compat/linux/design.md:290` mentions "the AArch64 table";
   - `tests/linux/linux.mk:9, 29` builds `lxhello` and the others for every ARCH (only `musl-gcc`/`hello_musl` is gated to
     x86-64, `:12-18`);
   - `run_boot_test.py:500-502` requires `LINUXTEST: PASS` with no arch condition.
6. `README.md:65-67` still says "the units proposed next are in the 1.1 roadmap". `docs/README.md:15-16` lists roadmaps 1.0
   and 1.1 but not `roadmap-1.2.md`, which `AGENTS.md:9` names as governing.
7. `docs/development.md:82` says `analyze` covers "every target source", but `Makefile:518, 525` analyzes only the kernel,
   loader, modules and pkg. libc and userland programs are not gated.
8. Doc phrasing: `docs/build/invariants.md:13-19` (B2) lists the resource headers as `stdint`, `stddef`, `stdbool` and
   `stdarg`, while `libc/include/sys/types.h:3-4` and `stdio.h:3-4` rely on them. Consistent, but `stdatomic.h` and
   `stdalign.h` are equally reachable and not mentioned.
9. `docs/libc/design.md:278-279` lists "Floating point once user FPU state is saved: `%f/%g/%e`, `strtod`, `<math.h>`" as
   future work, but `%f/%e/%g` already exist (`libc/README.md:18-21`). `strtod` and `math.h` are still absent.

### Open questions for the owner or the next agents

- Does the Nova runtime count as "library" under invariant L1 (`docs/libc/invariants.md:7-14`)? It decides whether Nova may
  call `cosmo_*` raw wrappers (aio, timers, exec) or must go through public libc only.
- Is Profile A allowed to target the musl/Linux personality (full POSIX, compiler-rt) for richer programs, or must it be
  native only? Invariant 7 forbids contaminating the native ABI. It does not forbid Linux-personality programs, and the base
  system ships some.
- Should missing libc pieces be added to `libc/` as an owner-chosen unit, Nova's runtime or not? Candidates:
  `aligned_alloc`, `math.h` (`Todo.md`), `poll`, `exec*` wrappers, `strtod`, `setjmp`. Adding them to `libc/` benefits C
  programs too.
- Is checked-in generated C acceptable (port route), or must generation happen in the build, which brings .NET into CI's
  image (`ci/Dockerfile`)? This needs an explicit decision given `plan.md:767`.
- Is CI time acceptable? The aarch64 job's median is about 51.7 min against a 70-min cap (`ci.yml:85-90`). Any added boot
  test or toolchain build counts against it.
- C1 needs a QEMU route that does not edit the tree. Verify whether a release image's cosmofs scratch disk survives boot, or
  whether a dedicated `QEMU_EXTRA` disk is needed (`docs/development.md:183-184, 284, 288`). Not tested in this audit.
- On AArch64 user code (no `-mno-outline-atomics`), does clang emit `__aarch64_*` outline-atomic helpers for the `none-elf`
  triple? If it does, links would fail because there are no builtins. Not verified (no build was run).
- What is the static-PIE outlook (SEC-5, BUILD-6)? Nova binaries inherit "no ASLR for code" until libc gains
  self-relocation.
- Kernel profile: the module ABI version (11) churns with every exported-struct change (`module.h:23`). A Nova kernel
  backend generating struct layouts must regenerate per ABI version. Module runtime semantics are another agent's scope.
