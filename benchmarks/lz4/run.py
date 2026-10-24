#!/usr/bin/env python3
"""
run.py -- builds and runs benchmarks/lz4/bench.c: CosmoOS's LZ4 codec against Nova's port, in one binary, both
compiled by the same C compiler at the same optimisation level, without sanitizers. Prints the machine, the
compiler, and the results. See README.md for how to read them.

Usage: python3 benchmarks/lz4/run.py [--cosmoos ~/dev/CosmoOS] [--cc clang] [--opt -O2] [--rounds 20]
"""

import argparse
import platform
import subprocess
import sys
from pathlib import Path

NOVA = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cosmoos", default=str(Path.home() / "dev" / "CosmoOS"))
    parser.add_argument("--cc", default="clang")
    parser.add_argument("--opt", default="-O2")
    parser.add_argument("--rounds", default="20")
    parser.add_argument("--novac", default=str(NOVA / "src" / "Nova.Cli" / "bin" / "Release" / "net10.0" / "novac"))
    args = parser.parse_args()
    cosmo = Path(args.cosmoos).expanduser()
    work = NOVA / "out" / "benchmarks" / "lz4"
    gen = work / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    subprocess.run([args.novac, "build", "--profile", "freestanding", "--emit", "c", "-o", gen,
                    "integrations/cosmoos/c6/lz4.nova"], cwd=NOVA, check=True)
    include = ["-I", cosmo / "kernel" / "include", "-I", cosmo / "tests" / "host" / "shim", "-I", gen]
    common = ["-std=c11", args.opt, "-DNDEBUG"]
    subprocess.run([args.cc, *common, *include, "-Dlz4_compress=c_lz4_compress", "-Dlz4_decompress=c_lz4_decompress",
                    "-c", cosmo / "kernel" / "core" / "lz4.c", "-o", work / "c_lz4.o"], check=True)
    exe = work / "bench"
    subprocess.run([args.cc, *common, *include, "-ffreestanding", "-fno-stack-protector", NOVA / "benchmarks" / "lz4" / "bench.c",
                    work / "c_lz4.o", gen / "main.c", gen / "nova_rt_core.c", NOVA / "integrations" / "cosmoos" / "c6" / "host_panic.c",
                    "-o", exe], check=True)
    version = subprocess.run([args.cc, "--version"], capture_output=True, text=True).stdout.splitlines()[0]
    print(f"machine: {platform.machine()} {platform.system()} {platform.release()}; compiler: {version}; {args.opt}")
    sys.stdout.flush()
    return subprocess.run([exe, args.rounds]).returncode


if __name__ == "__main__":
    sys.exit(main())
