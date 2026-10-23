#!/usr/bin/env python3
"""C3: collections and system calls in CosmoOS (docs/cosmoos/c3-report.md). Stage S0: CosmoOS stays read-only.

Builds two Nova programs as CosmoOS userland ELFs (as c1.py does):
- nwc (programs/nwc/nwc.nova), a word counter with BusyBox `wc`'s options and output format;
- c3-io (integrations/cosmoos/c3/c3-io.nova): program arguments, files and loopback TCP.

In the guest, a shell script runs nwc and BusyBox `wc` on the same inputs and arguments (text, empty,
no final newline, tabs, UTF-8, binary, 1 MiB, standard input, missing files, a directory, a bad
option) and compares them: stdout byte for byte, the exit status, and the first line of stderr
(BusyBox appends its usage text). c3-io's output is checked like a run-pass program.

Usage:
    python3 integrations/cosmoos/c3.py --arch x86_64
    python3 integrations/cosmoos/c3.py --arch aarch64 --ssh root@10.211.55.14
"""

import argparse
import random
import re
import shlex
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402

C3_DIR = Path(__file__).resolve().parent / "c3"

# (name, arguments, file for standard input or None). Names are file names in the data directory.
CASES = [
    ("default", ["x"], None),
    ("lines", ["-l", "x"], None),
    ("words-bytes", ["-w", "-c", "x"], None),
    ("stdin", [], "x"),
    ("stdin-lines", ["-l"], "x"),
    ("three-files", ["x", "y", "z"], None),
    ("lines-two-files", ["-l", "x", "y"], None),
    ("missing", ["nosuch"], None),
    ("missing-and-file", ["nosuch", "x"], None),
    ("all-columns", ["-c", "-m", "-L", "-l", "-w", "t"], None),
    ("combined-flags", ["-lw", "t"], None),
    ("dash", ["-"], "t"),
    ("directory", ["d"], None),
    ("bad-option", ["-q", "t"], None),
    ("empty", ["e"], None),
    ("whitespace-words", ["-w", "w"], None),
    ("utf8-chars", ["-m", "-c", "m"], None),
    ("utf8-longest", ["-L", "m"], None),
    ("tab-stops", ["-L", "tab"], None),
    ("tab-stops-2", ["-L", "tab2"], None),
    ("no-final-newline", ["-L", "-l", "z"], None),
    ("binary", ["-cmlwL", "bin"], None),
    ("large", ["big"], None),
    ("large-longest", ["-L", "-m", "big"], None),
    ("totals-longest", ["-L", "x", "y", "z", "t"], None),
    ("end-of-options", ["--", "x"], None),
]


def log(message):
    print(f"[c3] {message}", flush=True)


def make_data(directory):
    """The inputs, written once on the host and copied to the guest."""
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir(parents=True)
    rng = random.Random(3)
    files = {
        "x": b"a b\nc d e\n",
        "y": b"one\n",
        "z": b"no newline",
        "t": "a\tb\n12345678901\nhéllo wörld\n".encode(),
        "e": b"",
        "w": b"  a  b\t\tc\r\nd\fe\vf",
        "m": "é €\n🙂x\n".encode(),
        "tab": b"x\ty\n",
        "tab2": b"abcdefgh\tz\n",
        "bin": bytes(rng.randrange(256) for _ in range(4096)),
    }
    words = ["nova", "cosmo", "wc", "héllo", "\t", "日本", "line", "x" * 30]
    lines = []
    size = 0
    while size < 1 << 20:
        line = " ".join(rng.choice(words) for _ in range(rng.randrange(1, 12)))
        lines.append(line)
        size += len(line.encode()) + 1
    files["big"] = ("\n".join(lines) + "\n").encode()
    for name, content in files.items():
        (directory / f"c3-data-{name}").write_bytes(content)  # renamed to `name` in the guest's work directory
    return sorted(f"c3-data-{name}" for name in files)


def check_script(path):
    """The guest's comparison script: sh check.sh NWC DATA_DIRECTORY."""
    lines = [
        "#!/bin/sh",
        "NWC=$1",
        "rm -rf /tmp/c3work; mkdir -p /tmp/c3work || exit 1",
        "for f in \"$2\"/c3-data-*; do cp \"$f\" \"/tmp/c3work/${f##*/c3-data-}\" || exit 1; done",
        "cd /tmp/c3work && mkdir -p d || exit 1",
        "check() {",
        "  name=$1; input=$2; shift 2",
        "  if [ -n \"$input\" ]; then \"$NWC\" \"$@\" <\"$input\" >o1 2>e1; s1=$?; wc \"$@\" <\"$input\" >o2 2>e2; s2=$?",
        "  else \"$NWC\" \"$@\" >o1 2>e1; s1=$?; wc \"$@\" >o2 2>e2; s2=$?; fi",
        "  if cmp -s o1 o2 && [ \"$s1\" = \"$s2\" ] && [ \"$(head -n 1 e1)\" = \"$(head -n 1 e2)\" ]; then echo \"C3 PASS $name\"",
        "  else echo \"C3 FAIL $name (status $s1, wc $s2)\"; diff o1 o2 | head -n 10; head -n 1 e1; head -n 1 e2; fi",
        "}",
    ]
    for name, arguments, stdin in CASES:
        lines.append(f"check {name} '{stdin or ''}' {' '.join(shlex.quote(a) for a in arguments)}")
    lines.append("echo C3 DONE")
    path.write_text("\n".join(lines) + "\n")


def results(text):
    passed = [line.split()[2] for line in text.splitlines() if line.startswith("C3 PASS ")]
    failed = [line for line in text.splitlines() if line.startswith("C3 FAIL ")]
    return passed, failed, "C3 DONE" in text


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], required=True)
    parser.add_argument("--cosmoos", type=Path, default=Path.home() / "dev" / "CosmoOS")
    parser.add_argument("--timeout", type=int, default=1800, help="seconds for the QEMU boot")
    parser.add_argument("--ssh", metavar="USER@HOST", help="run on this running CosmoOS machine over ssh instead of QEMU")
    args = parser.parse_args()

    root = args.cosmoos.resolve()
    status_before = c1.git_status(root)
    head = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    out = c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release"
    work = c1.NOVA_ROOT / "out" / "cosmoos" / (f"c3-ssh-{args.arch}" if args.ssh else f"c3-{args.arch}")
    work.mkdir(parents=True, exist_ok=True)
    cosmo = c1.Cosmo(root, args.arch, out)
    log(f"CosmoOS {head}, ARCH={args.arch}, BUILD=release, OUT={out}")
    cosmo.make("image")
    cc, ld, prefix = cosmo.var("CC"), cosmo.var("LD"), cosmo.var("LLVM_PREFIX")
    cflags, ldflags = shlex.split(cosmo.var("USER_CFLAGS")), shlex.split(cosmo.var("USER_LDFLAGS"))
    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])

    nwc = c1.Program("nwc", c1.NOVA_ROOT / "programs" / "nwc", "nwc.nova")
    io = c1.Program("c3-io", C3_DIR, "c3-io.nova")
    elves = [c1.build_program(cosmo, program, work, cc, cflags, ld, ldflags) for program in (nwc, io)]
    for elf in elves:
        c1.check_elf(elf, prefix)
    data = work / "data"
    names = make_data(data)
    script = work / "check.sh"
    check_script(script)

    if args.ssh:
        ssh = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", args.ssh]
        machine = subprocess.run([*ssh, "uname -m -r -v"], check=True, capture_output=True, text=True).stdout.strip()
        log(f"running on {args.ssh}: {machine}")
        subprocess.run([*ssh, "for i in 1 2 3 4 5 6 7 8; do rm -rf /tmp/c3 2>/dev/null; [ -e /tmp/c3 ] || break; done; "
                              "mkdir -p /tmp/c3/data"], check=True)
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", *map(str, elves), str(script), f"{args.ssh}:/tmp/c3/"], check=True)
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", *[str(data / n) for n in names], f"{args.ssh}:/tmp/c3/data/"], check=True)
        compare = subprocess.run([*ssh, "sh /tmp/c3/check.sh /tmp/c3/nwc /tmp/c3/data"], capture_output=True, text=True,
                                 timeout=600).stdout
        io_run = subprocess.run([*ssh, "cd /tmp/c3 && env -i PATH=/bin:/usr/bin ./c3-io first second"], capture_output=True,
                                text=True, timeout=120)
        io_output, io_status = io_run.stdout.splitlines(), io_run.returncode
        qemu_status = None
    else:
        # The boot archive's files appear under /bin (as the programs do).
        extra = [("bin/c3-check.sh", script), *[(f"bin/{n}", data / n) for n in names]]
        image = c1.make_image(cosmo, work, elves, extra)
        commands = [Command("sh /bin/c3-check.sh /bin/nwc /bin"), Command("cd /tmp && c3-io first second; echo C3IO $?")]
        serial, qemu_status = c1.boot(cosmo, work, image, commands, args.timeout)
        compare = "\n".join(c1.guest_output(serial, commands[0].guest_name) or [])
        # The serial console interleaves the kernel's log lines with the program's output.
        io_lines = [line for line in c1.guest_output(serial, commands[1].guest_name) or []
                    if not re.match(r"\[ ?[A-Z]+\] ", line) and line != ""]
        io_status = next((int(line.split()[1]) for line in io_lines if line.startswith("C3IO ")), None)
        io_output = [line for line in io_lines if not line.startswith("C3IO ")]

    passed, failed, done = results(compare)
    for line in failed:
        log(line)
    io_ok = io_output == io.stdout and io_status == 0
    if not io_ok:
        log(f"c3-io: status {io_status}, output {io_output!r}, expected {io.stdout!r}")
    checks = {
        f"nwc matches BusyBox wc ({len(passed)}/{len(CASES)} cases)": done and not failed and len(passed) == len(CASES),
        "c3-io: arguments, files, loopback TCP and processes": io_ok,
        **({} if args.ssh else {"CosmoOS shut down cleanly (QEMU exit status 33)": qemu_status == 33}),
        "CosmoOS working tree unchanged": c1.git_status(root) == status_before,
    }
    for name, ok in checks.items():
        log(f"{'PASS' if ok else 'FAIL'}  {name}")
    return 0 if all(checks.values()) else 1


class Command:
    """A command typed at the guest shell (c1.boot uses `guest_name`)."""

    def __init__(self, text):
        self.guest_name = text


if __name__ == "__main__":
    sys.exit(main())
