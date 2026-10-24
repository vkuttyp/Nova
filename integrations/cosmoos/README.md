# CosmoOS integration (stage S0: out of tree)

Everything here runs **outside** the CosmoOS tree (`docs/cosmoos/build-integration.md`, stage S0).
The CosmoOS checkout is only read. Its Make writes into Nova's `out/cosmoos/` through `OUT=`,
and each script checks that CosmoOS's `git status` is unchanged.

| File | Purpose |
|---|---|
| `c1.py` | Milestone C1: builds CosmoOS's release image, compiles Nova programs with CosmoOS's own `USER_CFLAGS`/`USER_LDFLAGS`, links them against its `crt0.o` and `libc.a`, adds them to a copy of the boot archive, boots QEMU and runs them at the shell. `--corpus` also runs the whole run-pass and run-fail corpus in the guest |
| `c1/nova-hello.nova` | The C1 program: prints `NOVATEST: PASS` |
| `c1/nova-panic.nova` | Checks that a Nova panic reports its location and aborts under CosmoOS's libc |
| `c4.py` | Milestone C4: a freestanding Nova object (`c4/kdemo.nova`) compiled with CosmoOS's own `MODULE_CFLAGS`, joined with its `ld.lld` and `MODULE_LDFLAGS`, and checked against the module loader's ELF, relocation and symbol rules, the frame limit and `scripts/check-fpregs.sh`; eight negative controls. No QEMU needed (`docs/cosmoos/c4-report.md`) |
| `c4/nova_rt_kernel.c` | The kernel's `nova_panic`: a Nova panic is a kernel `panic()` |
| `c5.py` | Milestone C5: builds `c5/` into a signed, out-of-tree kernel module (`nova_c5`) with CosmoOS's own rules, adds it to a copy of the boot archive, boots QEMU and checks the in-kernel selftest's log lines (`docs/cosmoos/c5-report.md`) |
| `c6.py` | Milestone C6a: CosmoOS's own LZ4 host test and fuzz target, built once with `kernel/core/lz4.c` and once with `c6/lz4.nova`; a differential test of both (`c6/diff_lz4.c`); the Nova object under `MODULE_CFLAGS` with C4's checks. No QEMU (`docs/cosmoos/c6-report.md`; benchmark `benchmarks/lz4/`) |
| `c5/kernel.nova` | `KernelHeap`: Nova's `Allocator` over `kmalloc`/`kfree` |
| `c5/selftest.nova`, `c5/module.c` | The in-kernel selftest and the module around it (`COSMO_MODULE`, fault injection and accounting for `TestHeap`) |

Requirements: a CosmoOS checkout that builds on this host (`gmake check-tools` there), QEMU with
UEFI firmware, the .NET SDK, and Python 3.

```sh
python3 integrations/cosmoos/c1.py --arch x86_64 --corpus
python3 integrations/cosmoos/c1.py --arch aarch64 --corpus
```

Outputs:
- `out/cosmoos/<arch>-release/`: the CosmoOS build;
- `out/cosmoos/c1-<arch>/`: the Nova binaries, the image and `serial.log`;
- `flags.txt`: the exact flags used.

The image is signed with the CosmoOS developer key (`MODSIGN_KEY`). It is a test artefact, not a
release.
