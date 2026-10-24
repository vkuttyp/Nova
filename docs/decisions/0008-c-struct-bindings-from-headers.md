# ADR 0008 — C struct types are imported from the codebase's headers

- Status: **Accepted** (project owner, 2026-10-24: "take the freedom of modifying [profiles.md §3.2] to
  suit a better implementation. Nova in fact is multi-platform; it should work with the Linux codebase
  also"). The design below is the agent's under that instruction; the owner may revise it.
- Scope: how Nova code uses struct types that a C codebase defines: CosmoOS kernel and boot-protocol
  structs, Linux kernel structs, C library structs. It replaces the rule of `cosmoos/profiles.md` §3.2
  that kernel structs are "never re-declared" and are seen only as opaque types "with size and alignment
  queried at build time".

## Context

The old rule protected against a real hazard: a struct's layout in a kernel depends on the kernel's
configuration (`CONFIG_*` adds and removes fields, e.g. lockdep's `class` in `spinlock_t`), on the
architecture, and on the ABI version (CosmoOS module ABI 11). A layout written down in Nova would be
right for one build and silently wrong for another. But the rule also left Nova unable to *use* such a
struct: an opaque type can be passed along, not read. C6's survey found that three of its five
candidates (the framebuffer validator, the module ELF validator, GPT) are blocked for exactly this
reason, and every driver or subsystem Nova will touch later takes kernel structs. Nova is also meant to
work with other C codebases than CosmoOS, the Linux kernel above all, where the same hazard holds at a
much larger scale (layouts change with every `.config`).

## Decision

1. **Imported C types.** Inside an `extern "C"` block, `struct name { field: Type; … }` declares a view of
   a C type that a header defines, and `@header("path.h")` on the block names the headers that define
   it. The C spelling is `struct name`, or the `@ctype("name_t")` attribute's for a typedef. A
   declaration without fields (`struct name;`) imports the type opaquely.
2. **The layout is the header's, never Nova's.** Nova knows no size, alignment or offset of an imported
   type. Generated C includes the headers and uses the real type, so every field access is the C
   compiler's member access, compiled under the codebase's own flags. One Nova source works for every
   configuration and architecture that has the fields it names.
3. **Only the fields Nova uses are declared, with their C names, and each is checked.** Generated C
   asserts at compile time that every declared field exists and has exactly the declared type
   (`_Static_assert` on `_Generic` of the member and on its size). A configuration that removes or changes
   a field fails to compile, naming the field; it never miscompiles.
4. **Imported types live behind pointers and references.** Since Nova does not know their size, values of
   them are not Nova values: no locals, parameters or fields of the type itself, no copies; `*T`,
   `*mut T`, `borrow T` and `borrow mut T` only. Field places through them are read and written like any
   field (embedded imported structs are places too). Ownership of such objects stays with the C API that
   provides them (kernel objects, boot data), which is what the kernel-memory design wants anyway.
5. **Nothing is platform-specific.** The same mechanism imports CosmoOS's `struct cosmoboot_info`, Linux's
   `struct file`, or the C library's `struct stat`, each from that platform's headers under that
   platform's flags. A profile only decides which headers and flags a build uses.
6. **`@repr(C)` mirrors stay** for types Nova owns and exports (type-system §28.5), where Nova *defines*
   the layout and C code includes `nova_exports.h`.

## Consequences

- `cosmoos/profiles.md` §3.2, `cosmoos/abi.md` and `risks.md` P-3 change accordingly; the research audit
  (`research/cosmoos-kernel-audit.md`, implication 26) is left as written, since this satisfies it: types
  still come from the headers of the exact ABI.
- The interpreter cannot run code that touches an imported type's fields (it has no layout); such code is
  an interpreter fault, as C calls are (type-system §28.6).
- Field types must be types C and Nova both spell (integers, `bool`, raw pointers, imported types, and
  `@repr(C)` structs); C arrays, bit-fields, unions and anonymous members need later additions.
- It is specified in `type-system.md` §31 and implemented with the first component that needs it (C6b,
  2026-10-24). The first implementation leaves out embedded imported structs (a field whose type is
  an imported type itself): fields are integers, `bool` and raw pointers. Embedded members are a later
  addition, with arrays and the rest above.
