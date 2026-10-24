#!/usr/bin/env python3
"""
c6.py -- Nova milestone C6a: CosmoOS's LZ4 codec reimplemented in Nova, beside its C original, and held to the same
tests (docs/cosmoos/c6-report.md). CosmoOS is only read: its test sources are compiled out of tree.

Steps (no QEMU needed):
  1. novac compiles integrations/cosmoos/c6/lz4.nova (--profile freestanding) to C exporting lz4_compress and
     lz4_decompress, with exactly the prototypes of kernel/include/kernel/lz4.h;
  2. CosmoOS's own host test (tests/host/test_lz4.c) and fuzz target (tests/fuzz/fuzz_lz4.c, with its seeded
     driver) are built twice with CosmoOS's own HOST_CFLAGS (ASan, UBSan, -Werror): once with kernel/core/lz4.c,
     as its Make builds them, and once with the Nova object in its place; both must pass;
  3. the differential test (c6/diff_lz4.c) runs both codecs on the same inputs, valid and corrupted: identical
     bytes and results;
  4. the Nova object is compiled with CosmoOS's MODULE_CFLAGS and checked as C4 does (loader ELF rules,
     relocations, exports, frames, FP registers).
The benchmark is benchmarks/lz4/run.py.

Usage:
    python3 integrations/cosmoos/c6.py [--cosmoos ~/dev/CosmoOS] [--fuzz-runs 20000] [--cases 20000]
"""

import argparse
import shlex
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402
import c4  # noqa: E402

C6_DIR = Path(__file__).resolve().parent / "c6"
PACKAGE = "integrations/cosmoos/c6/lz4.nova"


def run(cmd, **kwargs):
    return subprocess.run([str(c) for c in cmd], capture_output=True, text=True, **kwargs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cosmoos", default=str(Path.home() / "dev" / "CosmoOS"))
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], default="aarch64", help="for the kernel-flag checks")
    parser.add_argument("--fuzz-runs", type=int, default=20000)
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    root = Path(args.cosmoos).expanduser().resolve()
    c4.COSMO = root
    status_before = c1.git_status(root)
    work = c1.NOVA_ROOT / "out" / "cosmoos" / "c6"
    gen = work / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    cosmo = c1.Cosmo(root, args.arch, c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release")
    cc = cosmo.var("HOST_CC")
    host_cflags, host_ldflags = shlex.split(cosmo.var("HOST_CFLAGS")), shlex.split(cosmo.var("HOST_LDFLAGS"))
    fuzz_cflags, fuzz_ldflags = shlex.split(cosmo.var("FUZZ_CFLAGS")), shlex.split(cosmo.var("FUZZ_LDFLAGS"))
    host_srcs = [root / s for s in shlex.split(cosmo.var("HOST_LZ4_SRCS"))]
    fuzz_srcs = [root / s for s in shlex.split(cosmo.var("FUZZ_LZ4_SRCS"))]
    original = root / "kernel" / "core" / "lz4.c"

    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])
    c1.run([c1.NOVAC, "build", "--profile", "freestanding", "--emit", "c", "-o", gen, PACKAGE], cwd=c1.NOVA_ROOT)
    nova = [gen / "main.c", gen / "nova_rt_core.c", C6_DIR / "host_panic.c"]
    nova_flags = ["-I", gen, "-ffreestanding", "-fno-stack-protector"]  # the freestanding profile's flags (§4.3)

    checks = {}

    def swap(sources):
        assert original in sources, f"{original} is not in the test's sources"
        return [s for s in sources if s != original] + nova

    for name, cflags, ldflags, sources, runner in [
        ("host test tests/host/test_lz4.c", host_cflags, host_ldflags, host_srcs, []),
        (f"fuzz target tests/fuzz/fuzz_lz4.c ({args.fuzz_runs} runs, seed 1)", fuzz_cflags, fuzz_ldflags, fuzz_srcs,
         ["-runs", args.fuzz_runs, "-seed", 1]),
    ]:
        for impl, srcs, extra in [("C original", sources, []), ("Nova port", swap(sources), nova_flags)]:
            exe = work / f"{Path(str(sources[-1] if 'fuzz' in name else sources[-1])).stem}-{impl.split()[0].lower()}-{len(name)}"
            built = run([cc, *cflags, *extra, *srcs, *ldflags, "-o", exe])
            if built.returncode != 0:
                checks[f"{name} with the {impl}"] = (False, built.stderr.strip().splitlines()[:8])
                continue
            out_dir = work / f"fuzz-out-{impl.split()[0].lower()}"
            out_dir.mkdir(exist_ok=True)
            ran = run([exe, *runner, *(["-out", out_dir] if runner else [])], timeout=1800,
                      env={"ASAN_OPTIONS": "detect_leaks=0", "PATH": "/usr/bin:/bin"})
            summary = (ran.stdout.strip().splitlines() or [""])[-1]
            checks[f"{name} with the {impl}: {summary[:90]}"] = (ran.returncode == 0, [] if ran.returncode == 0 else (ran.stdout + ran.stderr).splitlines()[-8:])

    diff_exe = work / "diff_lz4"
    built = run([cc, *host_cflags, *nova_flags, "-Dlz4_compress=c_lz4_compress", "-Dlz4_decompress=c_lz4_decompress",
                 "-c", original, "-o", work / "c_lz4.o"])
    if built.returncode == 0:
        built = run([cc, *host_cflags, *nova_flags, C6_DIR / "diff_lz4.c", work / "c_lz4.o", *nova, *host_ldflags, "-o", diff_exe])
    if built.returncode != 0:
        checks["differential test builds"] = (False, built.stderr.strip().splitlines()[:8])
    else:
        ran = run([diff_exe, args.cases, 1], timeout=1800, env={"ASAN_OPTIONS": "detect_leaks=0"})
        checks[f"differential test: {ran.stdout.strip().splitlines()[-1] if ran.stdout.strip() else 'no output'}"] = (
            ran.returncode == 0, (ran.stdout + ran.stderr).splitlines()[:8] if ran.returncode else [])

    # Kernel-ready: the Nova object under CosmoOS's module flags meets C4's checks.
    cflags = shlex.split(cosmo.var("MODULE_CFLAGS"))
    ld, objdump, limit = cosmo.var("LD"), cosmo.var("OBJDUMP"), int(cosmo.var("KERNEL_FRAME_LIMIT"))
    objects, usage, errors = [], [], []
    for source in [gen / "main.c", gen / "nova_rt_core.c", c4.C4_DIR / "nova_rt_kernel.c"]:  # nova_panic -> panic
        obj = work / f"kernel-{source.stem}.o"
        compiled = c4.compile_c(cosmo.var("CC"), cflags, source, obj, gen, ["-fstack-usage"])
        if compiled.returncode != 0:
            errors.append(compiled.stderr.strip())
        objects.append(obj)
        usage.append(obj.with_suffix(".su"))
    if not errors:
        joined = work / "nova_lz4.o"
        subprocess.run([ld, *shlex.split(cosmo.var("MODULE_LDFLAGS")), "-o", joined, *objects], check=True)
        problems, kinds, undefined = c4.all_problems(joined, args.arch, c4.kernel_exports(), objdump)
        frames, largest = c4.frame_problems(usage, limit)
        errors = problems + frames
        checks[f"{args.arch} object under MODULE_CFLAGS meets C4's checks (relocations {', '.join(kinds)}; undefined "
               f"{', '.join(undefined)}; largest frame {largest[0]} {largest[1]} B)"] = (not errors, errors)
    else:
        checks["compiles with CosmoOS's MODULE_CFLAGS"] = (False, errors)

    checks["CosmoOS working tree unchanged"] = (c1.git_status(root) == status_before, [])
    for name, (ok, details) in checks.items():
        print(f"[c6] {'PASS' if ok else 'FAIL'}  {name}")
        for detail in details[:10]:
            print(f"[c6]        {detail}")
    return 0 if all(ok for ok, _ in checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
