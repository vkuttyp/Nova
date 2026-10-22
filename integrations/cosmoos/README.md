# CosmoOS integration (stage S0: out of tree)

Everything here runs **outside** the CosmoOS tree (`docs/cosmoos/build-integration.md`, stage S0).
The CosmoOS checkout is only read. Its Make writes into Nova's `out/cosmoos/` through `OUT=`,
and each script checks that CosmoOS's `git status` is unchanged.

| File | Purpose |
|---|---|
| `c1.py` | Milestone C1: builds CosmoOS's release image, compiles Nova programs with CosmoOS's own `USER_CFLAGS`/`USER_LDFLAGS`, links them against its `crt0.o` and `libc.a`, adds them to a copy of the boot archive, boots QEMU and runs them at the shell. `--corpus` also runs the whole run-pass and run-fail corpus in the guest |
| `c1/nova-hello.nova` | The C1 program: prints `NOVATEST: PASS` |
| `c1/nova-panic.nova` | Checks that a Nova panic reports its location and aborts under CosmoOS's libc |

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
