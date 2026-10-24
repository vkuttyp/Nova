#!/usr/bin/env python3
"""
c5.py -- Nova milestone C5: Nova code allocating from CosmoOS's kernel heap, tested inside the kernel
(docs/cosmoos/c5-report.md).

Stage S0 of docs/cosmoos/build-integration.md: CosmoOS is only read. The test component is an out-of-tree kernel
module, built with CosmoOS's own rules and signed with its developer key, and added to a copy of the boot archive:
the kernel loads every modules/*.ko entry at boot (kernel/module/module.c, module_load_boot), so nothing in CosmoOS
changes. The kernel allocator is used as it is (kmalloc/kfree).

Steps, for one architecture:
  1. build CosmoOS's release image with its own Make (as c1.py), and ask it for MODULE_CFLAGS, MODULE_LDFLAGS,
     MODSIGN_KEY and the tools;
  2. novac compiles c5/kernel.nova (the KernelHeap binding) and c5/selftest.nova with --profile freestanding;
  3. the generated C, the freestanding core, c4/nova_rt_kernel.c (nova_panic -> panic) and c5/module.c (the
     module: COSMO_MODULE, init runs the selftest) are compiled with MODULE_CFLAGS, joined with ld.lld and
     MODULE_LDFLAGS, signed with scripts/modsign.py and checked with scripts/check-module-elf.py, as
     build/module.mk does for every CosmoOS module; then c4.py's checks run on it (loader ELF rules,
     relocations, exports, frames, FP registers);
  4. the module goes into the boot archive as modules/nova_c5.ko; CosmoOS boots in QEMU, and the serial log
     must show every selftest result PASS, nothing live at the end, and no boot module failing to load.

Usage:
    python3 integrations/cosmoos/c5.py --arch x86_64 [--cosmoos ~/dev/CosmoOS]
    python3 integrations/cosmoos/c5.py --arch aarch64
"""

import argparse
import re
import shlex
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402  (CosmoOS make, image, boot)
import c4  # noqa: E402  (the module loader's rules)

C5_DIR = Path(__file__).resolve().parent / "c5"
SOURCES = ["integrations/cosmoos/c5/kernel.nova", "integrations/cosmoos/c5/selftest.nova"]
TESTS = ["allocation", "ownership", "exhaustion-kmalloc", "exhaustion-injected", "cleanup-deep"]


def log(message):
    print(f"[c5] {message}", flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], required=True)
    parser.add_argument("--cosmoos", default=str(Path.home() / "dev" / "CosmoOS"))
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    root = Path(args.cosmoos).expanduser().resolve()
    c4.COSMO = root
    status_before = c1.git_status(root)

    out = c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release"
    work = c1.NOVA_ROOT / "out" / "cosmoos" / f"c5-{args.arch}"
    gen = work / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    cosmo = c1.Cosmo(root, args.arch, out)
    cosmo.make("image")
    cc, ld, objdump = cosmo.var("CC"), cosmo.var("LD"), cosmo.var("OBJDUMP")
    cflags, ldflags = shlex.split(cosmo.var("MODULE_CFLAGS")), shlex.split(cosmo.var("MODULE_LDFLAGS"))
    limit = int(cosmo.var("KERNEL_FRAME_LIMIT"))
    key = cosmo.var("MODSIGN_KEY")
    (work / "flags.txt").write_text(f"CC={cc}\nMODULE_CFLAGS={' '.join(cflags)}\nLD={ld}\nMODULE_LDFLAGS={' '.join(ldflags)}\n")

    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])
    c1.run([c1.NOVAC, "build", "--profile", "freestanding", "--emit", "c", "-o", gen, *SOURCES], cwd=c1.NOVA_ROOT)

    checks = {}
    objects, stack_usage, errors = [], [], []
    for source in [gen / "main.c", gen / "nova_rt_core.c", c4.C4_DIR / "nova_rt_kernel.c", C5_DIR / "module.c"]:
        obj = work / (source.stem + ".o")
        compiled = c4.compile_c(cc, cflags, source, obj, gen, ["-fstack-usage"])
        if compiled.returncode != 0:
            errors.append(f"{source.name}: {compiled.stderr.strip()}")
        objects.append(obj)
        stack_usage.append(obj.with_suffix(".su"))
    checks["compiles with CosmoOS's MODULE_CFLAGS"] = (not errors, errors)
    if errors:
        return report(checks, root, status_before)

    unsigned, module = work / "nova_c5.ko.unsigned", work / "nova_c5.ko"
    c1.run([ld, *ldflags, "-o", unsigned, *objects])
    c1.run([sys.executable, root / "scripts" / "modsign.py", "sign", "--key", key, "--in", unsigned, "--out", module])
    elf_check = subprocess.run([sys.executable, root / "scripts" / "check-module-elf.py", module, args.arch], capture_output=True, text=True)
    checks["scripts/check-module-elf.py (ET_REL, W^X, .cosmo.module, signature)"] = (elf_check.returncode == 0, elf_check.stderr.splitlines())
    elf = c4.Elf(unsigned)
    checks["ELF rules of the module loader (modelf.c)"] = (not c4.modelf_problems(elf, args.arch), c4.modelf_problems(elf, args.arch))
    relocations, kinds = c4.relocation_problems(elf, args.arch)
    checks[f"relocations applied by modreloc.c ({', '.join(kinds)})"] = (not relocations, relocations)
    exports = c4.kernel_exports()
    undefined = sorted({s["name"] for s in elf.symbols[1:] if s["shndx"] == c4.SHN_UNDEF and s["name"]})
    missing = [name for name in undefined if name not in exports]
    checks[f"undefined symbols exported by the kernel ({', '.join(undefined)})"] = (not missing, missing)
    forbidden = [p for p in c4.section_problems(elf) if "mutable global state" not in p]  # module.c's counters are the test's
    checks["no constructor, TLS or unwind sections"] = (not forbidden, forbidden)
    frames, largest = c4.frame_problems(stack_usage, limit)
    checks[f"every frame within {limit} bytes (largest: {largest[0]}, {largest[1]} bytes)"] = (not frames, frames)
    fp = c4.fp_problems(unsigned, objdump)
    checks["no floating-point or vector register (scripts/check-fpregs.sh)"] = (not fp, fp)

    image = c1.make_image(cosmo, work, [], extra=[("modules/nova_c5.ko", module)])
    serial, status = c1.boot(cosmo, work, image, [], args.timeout)
    lines = [line.strip() for line in serial.replace("\r", "\n").split("\n")]
    results = {}
    for line in lines:
        match = re.search(r"nova_c5: (\S+) (PASS|FAIL) \((-?\d+)\)", line)
        if match:
            results[match.group(1)] = (match.group(2) == "PASS", match.group(3))
    for name in TESTS:
        passed, detail = results.get(name, (False, "no result"))
        checks[f"in-kernel test {name} ({detail})"] = (passed, [])
    summary = next((line for line in lines if "nova_c5: all tests passed" in line), None)
    checks["module init: all tests passed, 0 TestHeap blocks live"] = (summary is not None and "; 0 TestHeap" in summary, [summary or "no summary line"])
    loaded = next((line for line in lines if re.search(r"module: \d+ boot module\(s\) loaded", line)), "")
    checks[f"boot modules: {loaded.split('module: ')[-1] if loaded else 'no line'}"] = (loaded.endswith(", 0 failed"), [])
    checks[f"CosmoOS shut down cleanly (QEMU exit status {status})"] = (status == 33, [])
    return report(checks, root, status_before)


def report(checks, root, status_before):
    checks["CosmoOS working tree unchanged"] = (c1.git_status(root) == status_before, [])
    for name, (ok, details) in checks.items():
        print(f"[c5] {'PASS' if ok else 'FAIL'}  {name}")
        for detail in details[:10]:
            print(f"[c5]        {detail}")
    return 0 if all(ok for ok, _ in checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
