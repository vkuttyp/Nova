#!/usr/bin/env python3
"""
c7.py -- Nova milestone C7: kernel concurrency (docs/cosmoos/c7-report.md). This script covers C7c and C7d, the
bindings: c7/sync.nova (SpinLock<T>, IrqSpinLock<T>, Mutex<T> over CosmoOS's own spinlock_t and struct mutex),
c7/kobject.nova (KRef<T> over its struct kobject) and c7/quiesce.nova (grace periods, deferred frees).
CosmoOS is only read.

Steps (no QEMU):
  1. negative cases: each c7/negative/*.nova, compiled with the bindings, must give exactly the diagnostics its
     `// error[N....]` annotations name (sleeping under a spin guard, allocating in an IRQ handler, reaching the
     data without a guard, a guard outliving its data's use);
  2. host test: the bindings and c7/demo.nova, compiled with CosmoOS's HOST_CFLAGS (ASan, UBSan, -Werror) against
     its own headers, linked with its HOST_COMMON_SRCS (the harness and its host spinlock shim,
     tests/host/shim_spinlock.c) and c7/host_kernel.c, run c7/host_test.c;
  3. kernel flags: the same package compiled with CosmoOS's MODULE_CFLAGS, with c4/nova_rt_kernel.c and
     c7/kernel_glue.c, joined and checked as C4 does (loader ELF rules, relocations, exports, frames, FP registers).

Usage:
    python3 integrations/cosmoos/c7.py [--arch aarch64|x86_64] [--cosmoos ~/dev/CosmoOS]
"""

import argparse
import re
import shlex
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402
import c4  # noqa: E402

C7_DIR = Path(__file__).resolve().parent / "c7"
PACKAGE = ["integrations/cosmoos/c5/kernel.nova", "integrations/cosmoos/c7/sync.nova", "integrations/cosmoos/c7/kobject.nova",
           "integrations/cosmoos/c7/quiesce.nova"]
ANNOTATION = re.compile(r"// error\[(N\d{4})\]")
DIAGNOSTIC = re.compile(r"^(?P<file>[^:\n]+):(?P<line>\d+):\d+: error\[(?P<code>N\d{4})\]", re.MULTILINE)


def run(cmd, **kwargs):
    return subprocess.run([str(c) for c in cmd], capture_output=True, text=True, **kwargs)


def negative_cases(checks):
    for case in sorted((C7_DIR / "negative").glob("*.nova")):
        relative = case.relative_to(c1.NOVA_ROOT)
        expected = {(n, m.group(1)) for n, text in enumerate(case.read_text().splitlines(), 1) for m in ANNOTATION.finditer(text)}
        result = run([c1.NOVAC, "check", "--profile", "freestanding", *PACKAGE, relative], cwd=c1.NOVA_ROOT)
        found = {(int(m.group("line")), m.group("code")) for m in DIAGNOSTIC.finditer(result.stdout + result.stderr)
                 if m.group("file") == str(relative)}
        others = [m.group(0) for m in DIAGNOSTIC.finditer(result.stdout + result.stderr) if m.group("file") != str(relative)]
        ok = found == expected and not others and result.returncode != 0
        details = [] if ok else [f"expected {sorted(expected)}, found {sorted(found)}", *others[:4]]
        checks[f"negative {case.name}: {', '.join(code for _, code in sorted(expected))}"] = (ok, details)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cosmoos", default=str(Path.home() / "dev" / "CosmoOS"))
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], default="aarch64", help="for the kernel-flag checks")
    args = parser.parse_args()
    root = Path(args.cosmoos).expanduser().resolve()
    c4.COSMO = root
    status_before = c1.git_status(root)
    cosmo = c1.Cosmo(root, args.arch, c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release")
    work = c1.NOVA_ROOT / "out" / "cosmoos" / "c7"
    gen = work / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])
    checks = {}

    negative_cases(checks)

    c1.run([c1.NOVAC, "build", "--profile", "freestanding", "--emit", "c", "-o", gen, *PACKAGE,
            "integrations/cosmoos/c7/demo.nova"], cwd=c1.NOVA_ROOT)
    nova_flags = ["-I", gen, "-ffreestanding", "-fno-stack-protector"]  # the freestanding profile's flags (§4.3)

    # Host: CosmoOS's host-test flags, headers, spinlock shim and harness.
    cc = cosmo.var("HOST_CC")
    host_cflags, host_ldflags = shlex.split(cosmo.var("HOST_CFLAGS")), shlex.split(cosmo.var("HOST_LDFLAGS"))
    # HOST_COMMON_SRCS: the harness, the spinlock shim, and what the harness needs (tests/host/host.mk).
    host_srcs = [gen / "main.c", gen / "nova_rt_core.c", c4.C4_DIR / "nova_rt_kernel.c", C7_DIR / "host_kernel.c",
                 C7_DIR / "host_test.c", root / "kernel" / "object" / "object.c",  # CosmoOS's own kobjects
                 *[root / s for s in shlex.split(cosmo.var("HOST_COMMON_SRCS"))]]
    exe = work / "host_test"
    built = run([cc, *host_cflags, *nova_flags, "-I", root / "tests" / "host", *host_srcs, *host_ldflags, "-o", exe])
    if built.returncode != 0:
        checks["host test builds with CosmoOS's HOST_CFLAGS"] = (False, built.stderr.strip().splitlines()[:10])
    else:
        ran = run([exe], timeout=600, env={"ASAN_OPTIONS": f"detect_leaks={0 if sys.platform == 'darwin' else 1}", "PATH": "/usr/bin:/bin"})  # no LSan on macOS
        lines = (ran.stdout + ran.stderr).strip().splitlines()
        checks[f"host test c7/host_test.c: {lines[-1] if lines else 'no output'}"] = (ran.returncode == 0, lines[-10:] if ran.returncode else [])

    # Kernel: CosmoOS's module flags, and C4's checks on the joined object.
    cflags = shlex.split(cosmo.var("MODULE_CFLAGS"))
    ld, objdump, limit = cosmo.var("LD"), cosmo.var("OBJDUMP"), int(cosmo.var("KERNEL_FRAME_LIMIT"))
    objects, usage, errors = [], [], []
    for source in [gen / "main.c", gen / "nova_rt_core.c", c4.C4_DIR / "nova_rt_kernel.c", C7_DIR / "kernel_glue.c"]:
        obj = work / f"kernel-{source.stem}.o"
        compiled = c4.compile_c(cosmo.var("CC"), cflags, source, obj, gen, ["-fstack-usage"])
        if compiled.returncode != 0:
            errors.append(compiled.stderr.strip())
        objects.append(obj)
        usage.append(obj.with_suffix(".su"))
    if not errors:
        joined = work / "nova_c7.o"
        subprocess.run([ld, *shlex.split(cosmo.var("MODULE_LDFLAGS")), "-o", joined, *objects], check=True)
        problems, kinds, undefined = c4.all_problems(joined, args.arch, c4.kernel_exports(), objdump)
        problems = [p for p in problems if "mutable global state" not in p]  # kernel_glue.c's counter is the test's (as in C5)
        frames, largest = c4.frame_problems(usage, limit)
        errors = problems + frames
        checks[f"{args.arch} object under MODULE_CFLAGS meets C4's checks (relocations {', '.join(kinds)}; undefined "
               f"{', '.join(undefined)}; largest frame {largest[0]} {largest[1]} B)"] = (not errors, errors)
    else:
        checks["compiles with CosmoOS's MODULE_CFLAGS"] = (False, [line for e in errors for line in e.splitlines()][:10])

    checks["CosmoOS working tree unchanged"] = (c1.git_status(root) == status_before, [])
    for name, (ok, details) in checks.items():
        print(f"[c7] {'PASS' if ok else 'FAIL'}  {name}")
        for detail in details[:10]:
            print(f"[c7]        {detail}")
    return 0 if all(ok for ok, _ in checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
