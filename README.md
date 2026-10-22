# Nova

Nova is a memory-safe systems programming language with C#/TypeScript-style syntax. It is
compiled ahead of time to portable C, with no garbage collector. Its first target platform is
[CosmoOS](https://github.com/vkuttyp/CosmoOS).

**Status:** early development. The design is complete (Milestone 0). The compiler
compiles the Milestone 1 subset (integers, booleans, functions, control flow) to portable C and
builds executables: `novac build hello.nova -o hello`. The same programs also run natively inside
CosmoOS on x86-64 and AArch64 ([C1 report](docs/cosmoos/c1-report.md)). Ownership, structs and
the heap come next.

```nova
namespace Demo;

fn gcd(a: u64, b: u64) -> u64 {
    var x = a;
    var y = b;
    while (y != 0) {
        let t = x % y;
        x = y;
        y = t;
    }
    return x;
}
```

- Design and status: [docs/README.md](docs/README.md)
- Roadmap: [docs/roadmap.md](docs/roadmap.md)
- Contributor and agent rules: [AGENTS.md](AGENTS.md)

Build and test (requires the .NET 10 SDK):

```sh
dotnet build Nova.slnx
dotnet test Nova.slnx
```
