/* nova_panic for the host builds of C6 (CosmoOS's host test, fuzz target, differential test, benchmark): a Nova
 * panic aborts with the hosted runtime's message (cosmoos/profiles.md §4.2). */
#include <stdio.h>
#include <stdlib.h>

#include "nova/rt.h"

_Noreturn void nova_panic(nv_panic_kind kind, nv_loc loc) {
    fprintf(stderr, "nova panic: %s at %s:%u:%u\n", nova_panic_reason(kind), loc.file, (unsigned)loc.line, (unsigned)loc.column);
    abort();
}
