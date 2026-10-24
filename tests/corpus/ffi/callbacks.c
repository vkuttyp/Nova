/* The C half of ffi/callbacks.nova: function pointers in both directions. */
#include "nova_exports.h"

typedef int32_t (*int_fn)(int32_t);

int32_t c_apply(int_fn f, int32_t x);
int_fn c_pick(bool negate);
void c_handler(struct Handler *out);

static int32_t c_negate(int32_t v) {
    return -v;
}

int32_t c_apply(int_fn f, int32_t x) {
    return f(x);
}

int_fn c_pick(bool negate) {
    return negate ? c_negate : cb_plus_one;
}

void c_handler(struct Handler *out) {
    out->run = cb_triple;
    out->base = 10;
}
