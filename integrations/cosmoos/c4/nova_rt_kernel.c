/*
 * Nova's panic hook for CosmoOS kernel code (C4; docs/cosmoos/profiles.md §3.3 and §4.2): a Nova panic
 * (overflow, bounds, unwrap, ...) is a kernel panic with Nova's reason and source location. Compiled with
 * CosmoOS's own module flags; uses only `panic`, which the kernel exports (kernel/core/panic.c).
 */
#include <kernel/panic.h>

#include "nova/rt.h"

_Noreturn void nova_panic(nv_panic_kind kind, nv_loc loc) {
    panic("nova panic: %s at %s:%u:%u", nova_panic_reason(kind), loc.file, (unsigned)loc.line, (unsigned)loc.column);
}
