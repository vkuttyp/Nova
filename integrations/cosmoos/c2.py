#!/usr/bin/env python3
"""C2: safe allocation in CosmoOS (docs/cosmoos/c2-report.md). Stage S0, like C1: CosmoOS stays read-only.

Builds every run-pass and run-fail program as a CosmoOS userland ELF (as c1.py --corpus does),
then boots CosmoOS in QEMU and runs, at the shell prompt:

1. every program once, checking stdout, exit status and panic messages (allocation and
   destruction of the M2/M3 features);
2. for every run-pass program that allocates, the guest-side fault-injection sweep:
   - a baseline run with NOVA_ALLOC_REPORT=1, which must report the host's attempt count and live=0;
   - runs with NOVA_FAIL_ALLOC_AT=k, each ending with live=0 or the unwrap panic (status 134).

Usage:
    python3 integrations/cosmoos/c2.py --arch x86_64 [--cosmoos ~/dev/CosmoOS]
    python3 integrations/cosmoos/c2.py --arch aarch64
"""

import argparse
import re
import shlex
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402  (the C1 building blocks: CosmoOS make, ELF build and checks, image, boot)

SWEEP_LIMIT = 40
REPORT = re.compile(r"nova alloc: attempts=(\d+) live=(\d+)")


def log(message):
    print(f"[c2] {message}", flush=True)


class Command:
    """A shell command for the guest; `guest_name` is what c1.boot types and c1.guest_output looks for."""

    def __init__(self, program, fail_at):
        self.program, self.fail_at = program, fail_at
        self.guest_name = f"NOVA_ALLOC_REPORT=1 NOVA_FAIL_ALLOC_AT={fail_at} {program.guest_name}"


def host_attempts(program, work):
    """Builds the program for the host and returns its allocation attempts, or 0 if it never allocates."""
    gen = work / "host" / program.guest_name
    subprocess.run([c1.NOVAC, "build", "--emit", "c", "-o", gen, program.relative], cwd=program.base, check=True,
                   capture_output=True)
    if "nova_alloc(" not in (gen / "main.c").read_text():
        return 0
    exe = gen / "program"
    subprocess.run([c1.NOVAC, "build", "-o", exe, program.relative], cwd=program.base, check=True, capture_output=True)
    run = subprocess.run([exe], capture_output=True, text=True, env={"NOVA_ALLOC_REPORT": "1"}, timeout=120)
    match = REPORT.search(run.stderr)
    if not match or match.group(2) != "0":
        raise SystemExit(f"{program.relative}: host baseline failed:\n{run.stderr}")
    return int(match.group(1))


def failure_points(attempts):
    """Every attempt, or for more than SWEEP_LIMIT: the first 10, the last 10 and 20 evenly spaced."""
    if attempts <= SWEEP_LIMIT:
        return list(range(1, attempts + 1))
    points = set(range(1, 11)) | set(range(attempts - 9, attempts + 1)) | {k * attempts // 21 for k in range(1, 21)}
    return sorted(points)


def check_sweep(serial, commands, attempts):
    failures = []
    for command in commands:
        lines = c1.guest_output(serial, command.guest_name)
        where = f"{command.program.relative} (fail at {command.fail_at})"
        if lines is None:
            failures.append(f"{where}: never ran")
            continue
        status = None
        report = None
        panics = []
        for line in lines:
            exited = re.match(rf"\[ INFO\] process: pid \d+ '{re.escape(command.program.guest_name)}' exited with status (\d+)", line)
            if exited:
                status = int(exited.group(1))
            elif REPORT.search(line):
                report = REPORT.search(line)
            elif line.startswith("nova panic: "):
                panics.append(line)
        if command.fail_at == 0:
            if report is None or int(report.group(1)) != attempts[command.program] or report.group(2) != "0":
                failures.append(f"{where}: baseline report {report and report.group(0)!r}, expected "
                                f"attempts={attempts[command.program]} live=0")
            continue
        if panics:
            if not all(p.startswith("nova panic: unwrapped an Err value") for p in panics) or status != 134:
                failures.append(f"{where}: unexpected panic {panics!r} (status {status})")
        elif report is None:
            failures.append(f"{where}: no allocation report (status {status})")
        elif report.group(2) != "0":
            failures.append(f"{where}: {report.group(2)} allocation(s) leaked")
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], required=True)
    parser.add_argument("--cosmoos", type=Path, default=Path.home() / "dev" / "CosmoOS")
    parser.add_argument("--timeout", type=int, default=3600, help="seconds for the QEMU boot")
    args = parser.parse_args()

    root = args.cosmoos.resolve()
    status_before = c1.git_status(root)
    head = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    out = c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release"
    work = c1.NOVA_ROOT / "out" / "cosmoos" / f"c2-{args.arch}"
    work.mkdir(parents=True, exist_ok=True)
    cosmo = c1.Cosmo(root, args.arch, out)
    log(f"CosmoOS {head}, ARCH={args.arch}, BUILD=release, OUT={out}")
    cosmo.make("image")

    cc, ld, prefix = cosmo.var("CC"), cosmo.var("LD"), cosmo.var("LLVM_PREFIX")
    cflags, ldflags = shlex.split(cosmo.var("USER_CFLAGS")), shlex.split(cosmo.var("USER_LDFLAGS"))
    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])

    programs = []
    for kind in ("run-pass", "run-fail"):
        for source in sorted((c1.CORPUS_DIR / kind).glob("*.nova")):
            programs.append(c1.Program(f"nv-{source.stem}", c1.CORPUS_DIR, f"{kind}/{source.name}"))

    attempts = {}
    for program in programs:
        if program.relative.startswith("run-pass/") and (n := host_attempts(program, work)) > 0:
            attempts[program] = n
    log("allocating programs: " + ", ".join(f"{p.guest_name} ({n} attempts)" for p, n in attempts.items()))
    sweep = [Command(p, k) for p, n in attempts.items() for k in [0, *failure_points(n)]]

    elves = [c1.build_program(cosmo, program, work, cc, cflags, ld, ldflags) for program in programs]
    for elf in elves:
        c1.check_elf(elf, prefix)
    image = c1.make_image(cosmo, work, elves)
    # The plain runs come first: the shell keeps leading assignments set (c2-report.md, findings).
    serial, qemu_status = c1.boot(cosmo, work, image, [*programs, *sweep], args.timeout)

    corpus_failures = c1.check_corpus(serial, programs)
    sweep_failures = check_sweep(serial, sweep, attempts)
    for failure in corpus_failures + sweep_failures:
        log(f"failure: {failure}")
    checks = {
        f"corpus runs in the guest ({len(programs)} programs)": not corpus_failures,
        f"guest fault-injection sweep ({len(attempts)} programs, {len(sweep)} runs)": not sweep_failures,
        "CosmoOS shut down cleanly (QEMU exit status 33)": qemu_status == 33,
        "CosmoOS working tree unchanged": c1.git_status(root) == status_before,
    }
    for name, ok in checks.items():
        log(f"{'PASS' if ok else 'FAIL'}  {name}")
    log(f"QEMU exit status {qemu_status}; serial log {work / 'serial.log'}")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
