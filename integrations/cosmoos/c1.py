#!/usr/bin/env python3
"""
c1.py -- Nova milestone C1: a Nova program running natively inside CosmoOS under QEMU.

Stage S0 of docs/cosmoos/build-integration.md: everything happens out of tree. The CosmoOS
checkout is only read, and its build writes only into Nova's out/ directory (OUT=...). The script
checks that `git status` in CosmoOS is the same before and after.

Steps, for one architecture:
  1. build CosmoOS's release image and libc with its own Make, into out/cosmoos/<arch>-release;
  2. ask that Make for USER_CFLAGS, USER_LDFLAGS and the tool paths (never copied by hand);
  3. compile tests programs with novac (--emit c), then with CosmoOS's clang flags, and link
     against CosmoOS's crt0.o, libc.a and userland/user.ld;
  4. check the ELF: the CosmoOS note (native personality), W^X segments, no undefined symbols;
  5. rebuild the boot archive with CosmoOS's mkbootarchive.py: the original entries plus
     bin/nova-hello and bin/nova-panic. Re-sign the manifest with the dev key (mkmanifest.py),
     and make the image (mkimage.sh) in out/cosmoos/c1-<arch>;
  6. boot it with scripts/qemu-run.sh, type each program's name at the shell, check the output,
     then `exit 0`.

With --corpus, every program in tests/corpus/run-pass and run-fail is also built (as nv-<name>)
and run in the guest. Its stdout, exit status (from the kernel's "exited with status" log line)
and panic location are checked against the same `// expect-*` directives as the host tests.

Usage:
    python3 integrations/cosmoos/c1.py --arch x86_64 [--corpus] [--cosmoos ~/dev/CosmoOS]
    python3 integrations/cosmoos/c1.py --arch aarch64 [--corpus]
"""

import argparse
import os
import re
import shlex
import shutil
import struct
import subprocess
import sys
import tarfile
import time
from pathlib import Path

NOVA_ROOT = Path(__file__).resolve().parents[2]
C1_DIR = Path(__file__).resolve().parent / "c1"
CORPUS_DIR = NOVA_ROOT / "tests" / "corpus"
NOVAC = NOVA_ROOT / "src" / "Nova.Cli" / "bin" / "Release" / "net10.0" / "novac"
PROMPT = b"\ncosmo$ "
DIRECTIVE = re.compile(r"// expect-(stdout|exit|panic):(?: (.*))?$")


class Program:
    """A Nova program to run in the guest. `relative` is passed to novac from `base` (it appears in panics)."""

    def __init__(self, guest_name, base, relative):
        self.guest_name, self.base, self.relative = guest_name, base, relative
        self.stdout, self.exit_code, self.panic = [], 0, None
        for number, line in enumerate((base / relative).read_text().splitlines(), start=1):
            match = DIRECTIVE.search(line)
            if not match:
                continue
            kind, value = match.group(1), match.group(2) or ""
            if kind == "stdout":
                self.stdout.append(value)
            elif kind == "exit":
                self.exit_code = int(value)
            else:
                self.panic = f"nova panic: {value} at {relative}:{number}:"
                self.exit_code = 134


def log(message):
    print(f"[c1] {message}", flush=True)


def run(cmd, **kwargs):
    log("$ " + " ".join(str(c) for c in cmd))
    return subprocess.run([str(c) for c in cmd], check=True, **kwargs)


class Cosmo:
    def __init__(self, root, arch, out):
        self.root, self.arch, self.out = root, arch, out
        self.base = ["gmake", "-s", "--no-print-directory", "-C", str(root),
                     f"ARCH={arch}", "BUILD=release", f"OUT={out}"]

    def make(self, *targets):
        run(self.base + list(targets))

    def var(self, name):
        """The value of a CosmoOS Make variable, via a pattern rule passed with --eval."""
        result = subprocess.run(
            self.base + ["--eval", 'nova-print-%: ; @printf "%s\\n" "$($*)"', f"nova-print-{name}"],
            check=True, capture_output=True, text=True)
        return result.stdout.strip()


def git_status(root):
    return subprocess.run(["git", "-C", str(root), "status", "--porcelain"],
                          check=True, capture_output=True, text=True).stdout


def build_program(cosmo, program, work, cc, cflags, ld, ldflags):
    name = program.guest_name
    gen = work / "gen" / name
    if gen.exists():
        shutil.rmtree(gen)
    # A relative path: it is embedded in panic messages and must not leak host paths.
    run([NOVAC, "build", "--emit", "c", "-o", gen, program.relative], cwd=program.base)
    objects = []
    for source in sorted(gen.glob("*.c")):
        obj = source.with_suffix(".o")
        run([cc, *cflags, "-I", gen, "-c", source, "-o", obj])
        objects.append(obj)
    elf = work / "bin" / name
    elf.parent.mkdir(exist_ok=True)
    crt0 = cosmo.out / "libc" / "src" / "arch" / cosmo.arch / "crt0.o"
    libc = cosmo.out / "libc" / "libc.a"
    run([ld, *ldflags, "-T", cosmo.root / "userland" / "user.ld", "-o", elf, crt0, *objects, libc])
    return elf


def elf_segments_and_notes(path):
    """Program headers and note names of an ELF64 little-endian file, parsed directly (no readelf needed)."""
    data = Path(path).read_bytes()
    if data[:4] != b"\x7fELF" or data[4] != 2 or data[5] != 1:
        raise SystemExit(f"{path}: not an ELF64 little-endian file")
    e_phoff, = struct.unpack_from("<Q", data, 0x20)
    e_phentsize, e_phnum = struct.unpack_from("<HH", data, 0x36)
    segments, notes = [], []
    for i in range(e_phnum):
        p_type, p_flags, p_offset, _, _, p_filesz = struct.unpack_from("<IIQQQQ", data, e_phoff + i * e_phentsize)
        segments.append((p_type, p_flags))
        if p_type == 4:  # PT_NOTE
            pos, end = p_offset, p_offset + p_filesz
            while pos + 12 <= end:
                namesz, descsz, note_type = struct.unpack_from("<III", data, pos)
                name = data[pos + 12:pos + 12 + namesz].rstrip(b"\0").decode("ascii", "replace")
                notes.append((name, note_type))
                pos += 12 + ((namesz + 3) & ~3) + ((descsz + 3) & ~3)
    return segments, notes


def check_elf(elf, prefix):
    segments, notes = elf_segments_and_notes(elf)
    if not any(name == "CosmoOS" for name, _ in notes):
        raise SystemExit(f"{elf}: no CosmoOS note ({notes}); it would run under the Linux personality")
    loads = [flags for kind, flags in segments if kind == 1]  # PT_LOAD
    if any(flags & 0x2 and flags & 0x1 for flags in loads):  # PF_W and PF_X
        raise SystemExit(f"{elf}: a PT_LOAD segment is both writable and executable")
    gnu_stack = [flags for kind, flags in segments if kind == 0x6474E551]
    if not gnu_stack or gnu_stack[0] & 0x1:
        raise SystemExit(f"{elf}: missing or executable PT_GNU_STACK")
    undefined = subprocess.run([f"{prefix}llvm-nm", "-u", elf], check=True, capture_output=True, text=True).stdout.strip()
    if undefined:
        raise SystemExit(f"{elf}: undefined symbols:\n{undefined}")
    log(f"{elf.name}: CosmoOS note present, {len(loads)} PT_LOAD segments without W+X, "
        f"non-executable stack, no undefined symbols")


def make_image(cosmo, work, elves):
    """The CosmoOS boot archive plus the Nova programs, signed and imaged with CosmoOS's scripts."""
    staging = work / "archive"
    if staging.exists():
        shutil.rmtree(staging)
    staging.mkdir()
    entries = []
    with tarfile.open(cosmo.out / "boot.tar") as archive:
        for index, member in enumerate(archive.getmembers()):
            if member.issym():
                entries.append(f"{member.name}@{member.linkname}")
            else:
                path = staging / f"{index:04d}"
                path.write_bytes(archive.extractfile(member).read())
                entries.append(f"{member.name}={path}")
    entries += [f"bin/{elf.name}={elf}" for elf in elves]

    boot_tar = work / "boot.tar"
    log(f"mkbootarchive.py {boot_tar} <{len(entries)} entries: CosmoOS's own plus {len(elves)} Nova programs>")
    subprocess.run([sys.executable, str(cosmo.root / "scripts" / "mkbootarchive.py"), str(boot_tar), *entries], check=True)
    cmdline = cosmo.out / "cmdline"
    kernel = Path(cosmo.var("KERNEL_ELF"))
    loader = Path(cosmo.var("LOADER_EFI"))
    manifest = work / "manifest"
    run([sys.executable, cosmo.root / "scripts" / "mkmanifest.py", "sign", "--key", cosmo.var("MODSIGN_KEY"),
         "--out", manifest, kernel, boot_tar, cmdline])
    image = work / "cosmoos-nova.img"
    run([cosmo.root / "scripts" / "mkimage.sh", image, loader, kernel, boot_tar, cmdline, manifest])
    return image


def boot(cosmo, work, image, programs, timeout):
    serial_log = work / "serial.log"
    env = dict(os.environ, ARCH=cosmo.arch, QEMU_TESTDISK=str(work / "testdisk.img"),
               QEMU_RMDISK=str(work / "rmdisk.img"), QEMU_VCON=str(work / "vcon.log"))
    commands = [p.guest_name for p in programs] + ["exit 0"]
    with open(serial_log, "wb") as out:
        proc = subprocess.Popen([cosmo.root / "scripts" / "qemu-run.sh", image], stdin=subprocess.PIPE,
                                stdout=out, stderr=subprocess.STDOUT, env=env, cwd=work)
        deadline = time.monotonic() + timeout
        try:
            for count, command in enumerate(commands, start=1):
                while serial_log.read_bytes().count(PROMPT) < count:
                    if proc.poll() is not None or time.monotonic() > deadline:
                        raise SystemExit(f"no shell prompt before '{command}'; see {serial_log}")
                    time.sleep(0.2)
                log(f"guest$ {command}")
                proc.stdin.write((command + "\n").encode())
                proc.stdin.flush()
            proc.wait(timeout=max(1, deadline - time.monotonic()))
        finally:
            if proc.poll() is None:
                proc.kill()
    return serial_log.read_bytes().decode("utf-8", "replace"), proc.returncode


def guest_output(serial, name):
    """The guest's output for one command: the lines between its prompt and the next prompt."""
    text = serial.replace("\r\n", "\n").replace("\r", "\n")
    start = text.find(f"\ncosmo$ {name}\n")
    if start < 0:
        return None
    start += len(f"\ncosmo$ {name}\n")
    end = text.find("\ncosmo$ ", start)
    return text[start:end if end >= 0 else len(text)].split("\n")


def check_corpus(serial, programs):
    failures = []
    for program in programs:
        lines = guest_output(serial, program.guest_name)
        if lines is None:
            failures.append(f"{program.relative}: never ran")
            continue
        status = None
        output = []
        for line in lines:
            exited = re.match(rf"\[ INFO\] process: pid \d+ '{re.escape(program.guest_name)}' exited with status (\d+)", line)
            if exited:
                status = int(exited.group(1))
            elif line.startswith("[ INFO]") or line in ("abort()",) or line.startswith("sh: "):
                continue  # kernel log lines, libc's abort() note, the shell's job report
            elif program.panic and line.startswith("nova panic: "):
                if not line.startswith(program.panic):
                    failures.append(f"{program.relative}: panic message {line!r}, expected {program.panic!r}...")
                program.panic = None  # seen
            else:
                output.append(line)
        while output and output[-1] == "":
            output.pop()
        if output != program.stdout:
            failures.append(f"{program.relative}: stdout {output!r}, expected {program.stdout!r}")
        if status != program.exit_code:
            failures.append(f"{program.relative}: exit status {status}, expected {program.exit_code}")
        if program.panic:
            failures.append(f"{program.relative}: no panic message (expected {program.panic!r})")
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], required=True)
    parser.add_argument("--cosmoos", type=Path, default=Path.home() / "dev" / "CosmoOS")
    parser.add_argument("--timeout", type=int, default=1800, help="seconds for the QEMU boot")
    parser.add_argument("--corpus", action="store_true",
                        help="also build and run tests/corpus/run-pass and run-fail in the guest")
    args = parser.parse_args()

    root = args.cosmoos.resolve()
    status_before = git_status(root)
    out = NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release"
    work = NOVA_ROOT / "out" / "cosmoos" / f"c1-{args.arch}"
    work.mkdir(parents=True, exist_ok=True)
    cosmo = Cosmo(root, args.arch, out)

    log(f"CosmoOS {subprocess.run(['git', '-C', str(root), 'rev-parse', 'HEAD'], capture_output=True, text=True).stdout.strip()}"
        f", ARCH={args.arch}, BUILD=release, OUT={out}")
    cosmo.make("image")

    cc = cosmo.var("CC")
    ld = cosmo.var("LD")
    prefix = cosmo.var("LLVM_PREFIX")
    cflags = shlex.split(cosmo.var("USER_CFLAGS"))
    ldflags = shlex.split(cosmo.var("USER_LDFLAGS"))
    (work / "flags.txt").write_text(f"CC={cc}\nUSER_CFLAGS={' '.join(cflags)}\nLD={ld}\nUSER_LDFLAGS={' '.join(ldflags)}\n")
    log(f"USER_CFLAGS: {' '.join(cflags)}")

    run(["dotnet", "build", NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])
    programs = [Program("nova-hello", C1_DIR, "nova-hello.nova"), Program("nova-panic", C1_DIR, "nova-panic.nova")]
    if args.corpus:
        for kind in ("run-pass", "run-fail"):
            for source in sorted((CORPUS_DIR / kind).glob("*.nova")):
                programs.append(Program(f"nv-{source.stem}", CORPUS_DIR, f"{kind}/{source.name}"))

    elves = [build_program(cosmo, program, work, cc, cflags, ld, ldflags) for program in programs]
    for elf in elves:
        check_elf(elf, prefix)

    image = make_image(cosmo, work, elves)
    serial, qemu_status = boot(cosmo, work, image, programs, args.timeout)

    checks = {
        "nova-hello printed NOVATEST: PASS": "NOVATEST: PASS" in serial,
        "nova-hello computed gcd": "gcd(1071, 462) = 21" in serial,
        "nova-hello printed i64 min": "i64 min = -9223372036854775808" in serial,
        "nova-panic printed before panicking": "NOVAPANIC: before" in serial,
        "nova-panic reported overflow with location": re.search(r"nova panic: integer overflow at \S*nova-panic\.nova:5:23", serial) is not None,
    }
    corpus = [p for p in programs if p.base == CORPUS_DIR]
    if corpus:
        failures = check_corpus(serial, corpus)
        for failure in failures:
            log(f"corpus: {failure}")
        checks[f"run corpus in the guest ({len(corpus)} programs)"] = not failures
    status_after = git_status(root)
    checks["CosmoOS working tree unchanged"] = status_before == status_after

    for name, ok in checks.items():
        log(f"{'PASS' if ok else 'FAIL'}  {name}")
    log(f"QEMU exit status {qemu_status}; serial log {work / 'serial.log'}")
    return 0 if all(checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
