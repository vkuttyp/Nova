# C3 — Collections and system calls in CosmoOS (plan and report)

Status: **complete 2026-10-23** (results below). Roadmap acceptance: *files, sockets and processes in
Profile A, and a useful utility that runs in QEMU on both arches with output compared to the C or
BusyBox equivalent* (`roadmap.md`; prompt `cosmoos-native.md` C3: safe strings, slices, vectors,
file operations and socket operations, and a useful native CosmoOS utility). Stage S0 as in C1/C2:
out of tree, CosmoOS read-only. Script: `integrations/cosmoos/c3.py`.

## Plan

1. **Safe strings, slices and vectors** are M6 (`StringView`, `String`, `Slice`, `Vector`; type-system
   §16, §22–§24). They run inside CosmoOS through `c2.py`, which runs the whole corpus there.
2. **Files, sockets and program arguments** (type-system §26): `File`, `TcpListener`, `TcpStream`
   (move types closed by drop), `IoError`, `Process.argument`, `Console.read`/`writeBytes`/
   `writeError`. Runtime: `runtime/hosted/nova_rt_io.c`, public libc functions only (L1, OD-15).
3. **The utility: `nwc`** (`programs/nwc/nwc.nova`), a word counter with BusyBox `wc`'s options
   (`-c -m -l -w -L`, `--`, `-` for standard input) and output format, written in safe Nova. It uses
   `Vector<StringView>` of argument views (M6x), files, standard input and standard error.
4. **Comparison in the guest:** `c3.py` builds `nwc` and `c3-io` with CosmoOS's own toolchain and
   flags (as `c1.py`), copies 11 inputs (text, empty, no final newline, tabs, UTF-8, 4 KiB of seeded
   random bytes, 1 MiB of mixed text) and runs a shell script in the guest that runs `nwc ARGS` and
   BusyBox `wc ARGS` on 26 cases (files, standard input, several files with totals, a missing file,
   a directory, a bad option, every option combination used). Stdout must be identical byte for
   byte, the exit statuses equal, and the first line of stderr equal (BusyBox then prints its usage).
5. **`c3-io`** (`integrations/cosmoos/c3/c3-io.nova`): arguments, file create/write/read/remove, a
   missing file's error, loopback TCP in one thread (bind to port 0, connect against the listen
   backlog, accept, exchange data both ways, shut down a direction, end of stream), and processes
   (`Process.run`: a child's output in order with the parent's, exit statuses 0, 1 and 7, a missing
   program).

## Interfaces re-audited (CosmoOS code, `1dfa1f74`; read, not changed)

| Interface | Code | Finding |
|---|---|---|
| Files | `libc/include/fcntl.h`, `unistd.h` | POSIX `open(path, flags, ...)` with `O_RDONLY/O_WRONLY/O_CREAT/O_TRUNC`, `read`, `write`, `close`, `unlink` |
| Errors | `libc/include/errno.h` | `ENOENT`, `EACCES`, `EEXIST`, `EISDIR`, `ENOTDIR`, `EINVAL`, `EPIPE`, `ECONNREFUSED`, `EADDRINUSE`, `ENOMEM` all present |
| Sockets | `libc/include/sys/socket.h`, `netinet/in.h`, `libc/src/socket.c` | `socket/bind/listen/accept/connect/send/recv/shutdown/getsockname`. **`struct sockaddr` is CosmoOS's own**: a `uint16_t` family, the port in *host* order, 16 address bytes. There is **no `setsockopt`** and **no `MSG_NOSIGNAL`**; `SO_REUSEADDR` is not defined. The runtime selects the address encoding with `#ifdef COSMO_AF_INET` and guards the option calls |
| Arguments | `crt0` → `__libc_start` → `main(argc, argv, envp)` (`abi.md`) | the generated `main` now takes `argc`/`argv` and hands them to `nova_rt_args` |
| Processes | `libc/include/spawn.h`, `sys/wait.h`, `libc/src/process.c` | **no `posix_spawn`**: CosmoOS's own `spawnvp(file, argv, handles, n)` (handles carry rights; none passed). `waitpid` exists, but the status is CosmoOS's encoding (`WEXITSTATUS(s)` is `s`; signals are 128 + n). The runtime uses `spawnvp` and the CosmoOS macros there, `posix_spawnp` elsewhere |
| BusyBox `wc` | `ports/busybox/applets` (BusyBox 1.37.0, Unicode on) | measured on the VM (below) |
| Boot archive | `scripts/mkbootarchive.py`, `kernel/core/bootarchive.c` | files added under `bin/` appear in the guest; files under a new top-level directory (`c3/…`) did not (observed; the archive's mount was not investigated further) |

## BusyBox `wc` as measured (the reference `nwc` follows)

- Columns: lines, words, characters (`-m`), bytes (`-c`), longest line (`-L`); default lines, words,
  bytes. Each column is padded to 9 characters and separated by one space, except when a single
  column is printed for a single input. Several files add a `total` line; a missing file prints
  `wc: NAME: No such file or directory` to stderr, is left out of the total, and makes the status 1.
  A directory prints a line of zeros, then `wc: NAME: Is a directory`.
- Per byte (C locale): printable ASCII has width 1 and is part of a word unless it is a space; `\t`
  moves to the next multiple of 8; `\n`, `\v`, `\f` and `\r` end the line for `-L`; those and the
  space end a word. **Every other byte (control characters and all non-ASCII bytes) is skipped**: no
  width, and it neither starts nor ends a word. `-m` counts the bytes that start a UTF-8 character.
  The first version of `nwc` treated non-ASCII bytes as word characters and only `\n` as a line end;
  the comparison found it on the random and 1 MiB inputs (24/26), and the rules above were then
  confirmed against BusyBox's behaviour.

## Results (run 2026-10-23)

| Run | Result |
|---|---|
| `c3.py --arch x86_64` (QEMU) | `nwc` = BusyBox `wc` on 26/26 cases; `c3-io` output as expected; CosmoOS shut down cleanly (status 33); CosmoOS tree unchanged |
| `c3.py --arch aarch64 --ssh root@10.211.55.14` (CosmoOS 1.0.10 VM) | 26/26; `c3-io` as expected; tree unchanged |
| `c2.py` x86-64 (QEMU) and AArch64 (VM) | the whole corpus (including `run-pass/io-errors.nova`) and the fault-injection sweep pass in the guest; see `testing.md` |
| Host | `ProgramTests`: `c3-io` in a temporary directory, and `nwc` on 17 cases recorded from BusyBox; corpus `io-errors`, `check-fail/io.nova`, `check-fail/io-moves.nova` |

Not done in C3 (recorded): pipes between processes, environment control and capturing a child's
output (`Process.run` inherits standard I/O); UDP and IPv6 sockets; non-blocking
I/O (CosmoOS has no `poll`/`select`, `profiles.md` §2.2); a `SIGPIPE`-free write on CosmoOS sockets
(no `MSG_NOSIGNAL` or `setsockopt` there: a write to a closed peer may raise `SIGPIPE`).
