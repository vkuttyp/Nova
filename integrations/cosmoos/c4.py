#!/usr/bin/env python3
"""
c4.py -- Nova milestone C4: a freestanding Nova object built as CosmoOS kernel code, and checked against the
rules CosmoOS's module loader enforces (docs/cosmoos/c4-report.md).

Stage S0 of docs/cosmoos/build-integration.md: the CosmoOS checkout is only read. Its Make is asked for
MODULE_CFLAGS, MODULE_LDFLAGS, the frame limit and the tool paths (never copied by hand); nothing is built
into its tree, and `git status` there must be the same before and after.

Steps, for one architecture:
  1. novac compiles integrations/cosmoos/c4/kdemo.nova with --profile freestanding --emit c;
  2. the generated C, the freestanding core runtime and c4/nova_rt_kernel.c (nova_panic -> the kernel's
     panic) are compiled with CosmoOS's MODULE_CFLAGS (its kernel flags: -Werror, its warning set,
     -Wframe-larger-than, -mgeneral-regs-only, the code model) plus -fstack-usage, and joined with
     `ld.lld $(MODULE_LDFLAGS)`, as build/module.mk joins a module's objects;
  3. the object is checked:
     - the ELF rules of kernel/module/modelf.c (ET_REL, the machine, RELA only, allocatable sections
       PROGBITS or NOBITS, W^X, no executable NOBITS, alignment at most a page, no COMMON or XINDEX
       symbols, no undefined local symbols) -- not the module metadata (.cosmo.module, the signature),
       which a module adds in C8;
     - relocation types against the ones kernel/arch/<arch>/modreloc.c applies, read from that file;
     - undefined symbols against the kernel's EXPORT_SYMBOL list, read from its sources;
     - no constructor, TLS or unwind sections, and no mutable global state (cosmoos/profiles.md §4.4);
     - every function's frame within the kernel limit (-fstack-usage), as -Wframe-larger-than also checks;
     - no floating-point or vector register, by CosmoOS's own scripts/check-fpregs.sh;
  4. negative controls: small objects that break one rule each must be rejected by the same checks, so a
     pass above means something.

Usage:
    python3 integrations/cosmoos/c4.py --arch x86_64 [--cosmoos ~/dev/CosmoOS]
    python3 integrations/cosmoos/c4.py --arch aarch64
"""

import argparse
import re
import shlex
import struct
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import c1  # noqa: E402  (CosmoOS make variables, git status, novac path)

C4_DIR = Path(__file__).resolve().parent / "c4"
PACKAGE = "integrations/cosmoos/c4/kdemo.nova"  # relative to the Nova root: it appears in panic locations

SHT_NULL, SHT_PROGBITS, SHT_SYMTAB, SHT_RELA, SHT_NOBITS, SHT_REL, SHT_DYNSYM = 0, 1, 2, 4, 8, 9, 11
SHF_WRITE, SHF_ALLOC, SHF_EXECINSTR = 1, 2, 4
SHN_UNDEF, SHN_ABS, SHN_COMMON, SHN_XINDEX = 0, 0xFFF1, 0xFFF2, 0xFFFF
STB_LOCAL = 0
MODELF_PAGE = 4096  # kernel/include/kernel/modelf.h
MACHINES = {"x86_64": 62, "aarch64": 183}
FORBIDDEN_SECTIONS = (".init_array", ".fini_array", ".ctors", ".dtors", ".tdata", ".tbss", ".eh_frame")


def log(message):
    print(f"[c4] {message}", flush=True)


class Elf:
    """The parts of an ELF64 relocatable object the module loader reads."""

    def __init__(self, path):
        data = path.read_bytes()
        self.data = data
        if data[:4] != b"\x7fELF" or data[4] != 2 or data[5] != 1:
            raise ValueError("not a little-endian ELF64")
        self.type, self.machine = struct.unpack_from("<HH", data, 16)
        shoff, = struct.unpack_from("<Q", data, 40)
        shentsize, shnum, shstrndx = struct.unpack_from("<HHH", data, 58)
        self.sections = []
        for i in range(shnum):
            name, kind, flags, _, offset, size, link, info, align, entsize = struct.unpack_from(
                "<IIQQQQIIQQ", data, shoff + i * shentsize)
            self.sections.append(dict(index=i, name_off=name, type=kind, flags=flags, offset=offset, size=size,
                                      link=link, info=info, align=align, entsize=entsize))
        names = self.sections[shstrndx]
        for s in self.sections:
            s["name"] = self._string(names, s["name_off"])
        self.symbols = []
        for s in self.sections:
            if s["type"] == SHT_SYMTAB:
                strtab = self.sections[s["link"]]
                for j in range(s["size"] // 24):
                    name, info, _, shndx, _, _ = struct.unpack_from("<IBBHQQ", data, s["offset"] + j * 24)
                    self.symbols.append(dict(name=self._string(strtab, name), bind=info >> 4, shndx=shndx))
        self.relocations = []  # (target section, type number, symbol name)
        symtab_names = [sym["name"] for sym in self.symbols]
        for s in self.sections:
            if s["type"] == SHT_RELA:
                target = self.sections[s["info"]]
                for j in range(s["size"] // 24):
                    _, info, _ = struct.unpack_from("<QQq", data, s["offset"] + j * 24)
                    symbol = info >> 32
                    self.relocations.append((target, info & 0xFFFFFFFF, symtab_names[symbol] if symbol < len(symtab_names) else "?"))

    def _string(self, table, offset):
        start = table["offset"] + offset
        return self.data[start:self.data.index(b"\0", start)].decode("ascii", "replace")


def modelf_problems(elf, arch):
    """kernel/module/modelf.c's modelf_validate, without the module metadata (C8)."""
    problems = []
    if elf.type != 1:
        problems.append("not a relocatable object (ET_REL)")
    if elf.machine != MACHINES[arch]:
        problems.append(f"e_machine {elf.machine} is not {arch}")
    for s in elf.sections[1:]:
        if s["type"] == SHT_REL:
            problems.append(f"{s['name']}: REL relocations unsupported (need RELA)")
        if s["type"] == SHT_DYNSYM:
            problems.append(f"{s['name']}: dynamic symbol table in a module")
        if s["type"] == SHT_RELA:
            if elf.sections[s["info"]]["flags"] & SHF_ALLOC and elf.sections[s["info"]]["type"] == SHT_NOBITS:
                problems.append(f"{s['name']}: relocations against a NOBITS section")
            continue
        if not s["flags"] & SHF_ALLOC:
            continue
        if s["type"] not in (SHT_PROGBITS, SHT_NOBITS):
            problems.append(f"{s['name']}: allocatable section of an unexpected type {s['type']}")
        w, x = s["flags"] & SHF_WRITE, s["flags"] & SHF_EXECINSTR
        if w and x:
            problems.append(f"{s['name']}: section is writable and executable (W^X)")
        if x and s["type"] == SHT_NOBITS:
            problems.append(f"{s['name']}: executable NOBITS section")
        align = s["align"] or 1
        if align & (align - 1) or align > MODELF_PAGE:
            problems.append(f"{s['name']}: section alignment {align} is not a power of two up to a page")
    for sym in elf.symbols[1:]:
        if sym["shndx"] == SHN_COMMON:
            problems.append(f"{sym['name']}: COMMON symbol (build with -fno-common)")
        if sym["shndx"] == SHN_XINDEX:
            problems.append(f"{sym['name']}: extended section index unsupported")
        if sym["shndx"] == SHN_UNDEF and sym["bind"] == STB_LOCAL and sym["name"]:
            problems.append(f"{sym['name']}: undefined local symbol")
    return problems


def relocation_names(arch):
    """Type numbers and names from the kernel's own headers: kernel/include/kernel/elf64.h, and the architecture's
    modreloc.h (AArch64 defines its types there)."""
    headers = [COSMO / "kernel" / "include" / "kernel" / "elf64.h", COSMO / "kernel" / "arch" / arch / "include" / arch / "modreloc.h"]
    text = "\n".join(h.read_text() for h in headers if h.exists())
    prefix = "R_X86_64_" if arch == "x86_64" else "R_AARCH64_"
    names = {}
    for name, value in re.findall(rf"#define\s+({prefix}\w+)\s+(0x[0-9a-fA-F]+|\d+)", text):
        names[int(value, 0)] = name
    return names


def accepted_relocations(arch):
    """The relocation types kernel/arch/<arch>/modreloc.c applies: its case labels and `type == R_…` tests."""
    text = (COSMO / "kernel" / "arch" / arch / "modreloc.c").read_text()
    prefix = "R_X86_64_" if arch == "x86_64" else "R_AARCH64_"
    return set(re.findall(rf"case\s+({prefix}\w+)\s*:", text)) | set(re.findall(rf"==\s*({prefix}\w+)", text))


def relocation_problems(elf, arch):
    names, accepted = relocation_names(arch), accepted_relocations(arch)
    problems, seen = [], set()
    for target, number, symbol in elf.relocations:
        if not target["flags"] & SHF_ALLOC:
            continue  # relocations for debug info: the loader ignores them (modelf.c)
        name = names.get(number, f"type {number}")
        seen.add(name)
        if name not in accepted:
            problems.append(f"relocation {name} against {symbol} in {target['name']} is not one modreloc.c applies")
    return problems, sorted(seen)


def kernel_exports():
    """Every name in an EXPORT_SYMBOL(...) of the CosmoOS sources (what a module may import)."""
    out = subprocess.run(["git", "-C", str(COSMO), "grep", "-h", "-o", r"EXPORT_SYMBOL\s*(\s*[A-Za-z_][A-Za-z0-9_]*\s*)"],
                         check=True, capture_output=True, text=True).stdout
    return {re.sub(r"EXPORT_SYMBOL\s*\(\s*|\s*\)", "", line) for line in out.splitlines()}


def section_problems(elf):
    problems = []
    for s in elf.sections:
        if any(s["name"] == f or s["name"].startswith(f + ".") for f in FORBIDDEN_SECTIONS):
            problems.append(f"{s['name']}: constructors, TLS and unwind tables are not allowed (cosmoos/profiles.md §4.4)")
        is_state = s["name"] in (".data", ".bss") or s["name"].startswith((".data.", ".bss."))
        if is_state and s["size"] > 0:
            problems.append(f"{s['name']}: {s['size']} bytes of mutable global state")
    return problems


def frame_problems(stack_usage_files, limit):
    problems, largest = [], ("", 0)
    for path in stack_usage_files:
        for line in path.read_text().splitlines():
            # "file:line:col:function<TAB>size<TAB>static"
            parts = line.split("\t")
            size = int(parts[1])
            largest = max(largest, (parts[0].rsplit(":", 1)[-1], size), key=lambda p: p[1])
            if size > limit or parts[2] != "static":
                problems.append(f"{parts[0]}: frame {size} bytes ({parts[2]}), over {limit} or not static")
    return problems, largest


def fp_problems(obj, objdump):
    result = subprocess.run(["sh", COSMO / "scripts" / "check-fpregs.sh", obj, objdump], capture_output=True, text=True)
    return [] if result.returncode == 0 else [line for line in result.stdout.splitlines() if "check-fpregs:" in line][:5]


def compile_c(cc, cflags, source, obj, include, extra=()):
    command = [cc, *cflags, *extra, "-I", include, "-c", source, "-o", obj]
    return subprocess.run([str(c) for c in command], capture_output=True, text=True)


def all_problems(obj, arch, exports, objdump):
    elf = Elf(obj)
    relocations, kinds = relocation_problems(elf, arch)
    undefined = sorted({sym["name"] for sym in elf.symbols[1:] if sym["shndx"] == SHN_UNDEF and sym["name"]})
    missing = [f"{name}: undefined, and not exported by the kernel" for name in undefined if name not in exports]
    return modelf_problems(elf, arch) + relocations + missing + section_problems(elf) + fp_problems(obj, objdump), kinds, undefined


def negative_controls(cc, cflags, work, arch, exports, objdump, ld, ldflags):
    """Objects that each break one rule; every one must be rejected (by the compiler or by the checks)."""
    controls = {
        "a floating-point operation (no -mgeneral-regs-only)": ("double twice(double x);\ndouble twice(double x) { return x * 2.5; }\n",
                                                               [f for f in cflags if f != "-mgeneral-regs-only"], []),
        "a COMMON symbol": ("int shared;\nint get(void);\nint get(void) { return shared; }\n", cflags, ["-fcommon"]),
        "thread-local storage": ("_Thread_local int t;\nint get(void);\nint get(void) { return t; }\n", cflags, []),
        "mutable global state": ("int counter = 1;\nint bump(void);\nint bump(void) { return ++counter; }\n", cflags, []),
        "unwind tables": ("int id(int x);\nint id(int x) { return x; }\n", cflags, ["-funwind-tables", "-fasynchronous-unwind-tables"]),
        "position-independent code (GOT relocations)": ("extern int far_away;\nint get(void);\nint get(void) { return far_away; }\n",
                                                        [f for f in cflags if f not in ("-fno-pic", "-mcmodel=kernel")], ["-fpic"]),
        "an undefined symbol the kernel does not export": ("void nova_not_exported(void);\nvoid call(void);\nvoid call(void) { nova_not_exported(); }\n",
                                                          cflags, []),
        "a frame larger than the kernel limit": ("void use(volatile char *p);\nvoid big(void);\nvoid big(void) { volatile char b[4096]; use(b); }\n",
                                                 cflags, []),
    }
    results = []
    for what, (source, flags, extra) in controls.items():
        name = re.sub(r"[^a-z]+", "-", what)[:40].strip("-")
        src, obj = work / f"control-{name}.c", work / f"control-{name}.o"
        src.write_text(source)
        compiled = compile_c(cc, flags, src, obj, work, extra)
        if compiled.returncode != 0:
            results.append((what, True, "rejected by the compiler: " + compiled.stderr.strip().splitlines()[0][:120]))
            continue
        joined = work / f"control-{name}.ko"
        subprocess.run([ld, *ldflags, "-o", joined, obj], check=True)
        problems, _, _ = all_problems(joined, arch, exports, objdump)
        results.append((what, bool(problems), "; ".join(problems)[:160] if problems else "NOT rejected"))
    return results


def main():
    global COSMO
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", choices=["x86_64", "aarch64"], required=True)
    parser.add_argument("--cosmoos", default=str(Path.home() / "dev" / "CosmoOS"))
    args = parser.parse_args()
    COSMO = Path(args.cosmoos).expanduser().resolve()
    status_before = c1.git_status(COSMO)

    work = c1.NOVA_ROOT / "out" / "cosmoos" / f"c4-{args.arch}"
    out = c1.NOVA_ROOT / "out" / "cosmoos" / f"{args.arch}-release"
    gen = work / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    cosmo = c1.Cosmo(COSMO, args.arch, out)
    cc, ld, objdump = cosmo.var("CC"), cosmo.var("LD"), cosmo.var("OBJDUMP")
    cflags, ldflags = shlex.split(cosmo.var("MODULE_CFLAGS")), shlex.split(cosmo.var("MODULE_LDFLAGS"))
    limit = int(cosmo.var("KERNEL_FRAME_LIMIT"))
    (work / "flags.txt").write_text(f"CC={cc}\nMODULE_CFLAGS={' '.join(cflags)}\nLD={ld}\nMODULE_LDFLAGS={' '.join(ldflags)}\n"
                                    f"KERNEL_FRAME_LIMIT={limit}\n")
    log(f"MODULE_CFLAGS: {' '.join(cflags)}")

    c1.run(["dotnet", "build", c1.NOVA_ROOT / "src" / "Nova.Cli", "-c", "Release", "-v", "quiet", "-nologo"])
    c1.run([c1.NOVAC, "build", "--profile", "freestanding", "--emit", "c", "-o", gen, PACKAGE], cwd=c1.NOVA_ROOT)

    objects, stack_usage, compile_errors = [], [], []
    for source in [gen / "main.c", gen / "nova_rt_core.c", C4_DIR / "nova_rt_kernel.c"]:
        obj = work / (source.stem + ".o")
        compiled = compile_c(cc, cflags, source, obj, gen, ["-fstack-usage"])
        if compiled.returncode != 0:
            compile_errors.append(f"{source.name}: {compiled.stderr.strip()}")
        objects.append(obj)
        stack_usage.append(obj.with_suffix(".su"))
    checks = {}
    checks["compiles with CosmoOS's MODULE_CFLAGS (-Werror, its warnings, -Wframe-larger-than)"] = (not compile_errors, compile_errors)
    if compile_errors:
        return report(checks, COSMO, status_before)

    joined = work / "nova_c4.o"
    c1.run([ld, *ldflags, "-o", joined, *objects])
    exports = kernel_exports()
    elf = Elf(joined)
    checks["ELF rules of the module loader (kernel/module/modelf.c)"] = (not modelf_problems(elf, args.arch), modelf_problems(elf, args.arch))
    relocations, kinds = relocation_problems(elf, args.arch)
    checks[f"relocations applied by kernel/arch/{args.arch}/modreloc.c ({', '.join(kinds)})"] = (not relocations, relocations)
    undefined = sorted({sym["name"] for sym in elf.symbols[1:] if sym["shndx"] == SHN_UNDEF and sym["name"]})
    missing = [name for name in undefined if name not in exports]
    checks[f"undefined symbols exported by the kernel ({', '.join(undefined)})"] = (not missing, missing)
    checks["no constructor, TLS or unwind sections; no mutable global state"] = (not section_problems(elf), section_problems(elf))
    frames, largest = frame_problems(stack_usage, limit)
    checks[f"every frame within {limit} bytes (largest: {largest[0]}, {largest[1]} bytes)"] = (not frames, frames)
    fp = fp_problems(joined, objdump)
    checks["no floating-point or vector register (scripts/check-fpregs.sh)"] = (not fp, fp)
    exported = sorted(sym["name"] for sym in elf.symbols[1:] if sym["shndx"] != SHN_UNDEF and sym["bind"] != STB_LOCAL)
    log(f"global symbols: {', '.join(exported)}")

    controls = negative_controls(cc, cflags, work, args.arch, exports, objdump, ld, ldflags)
    for what, rejected, why in controls:
        log(f"control: {what}: {why}")
    checks[f"negative controls rejected ({sum(r for _, r, _ in controls)}/{len(controls)})"] = (
        all(r for _, r, _ in controls), [w for w, r, _ in controls if not r])
    return report(checks, COSMO, status_before)


def report(checks, cosmo_root, status_before):
    checks["CosmoOS working tree unchanged"] = (c1.git_status(cosmo_root) == status_before, [])
    for name, (ok, details) in checks.items():
        print(f"[c4] {'PASS' if ok else 'FAIL'}  {name}")
        for detail in details[:10]:
            print(f"[c4]        {detail}")
    return 0 if all(ok for ok, _ in checks.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
