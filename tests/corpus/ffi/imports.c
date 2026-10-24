/* The C half of ffi/imports.nova: owns the devices and points that Nova sees through imports.types.h. */
#include <stdio.h>

#include "nova_exports.h"

struct imp_registry {
    int unused;
};

int32_t c_main(void);

int32_t c_main(void) {
    static struct imp_registry reg_a, reg_b;
    struct imp_device d3 = { 9, 0x3000, 12, true, "uart", 0, NULL, &reg_a };
    struct imp_device d2 = { 9, 0x2000, 7, false, "timer", 0, &d3, &reg_b };
    struct imp_device d1 = { 9, 0x1000, 5, true, "disk", 2, &d2, &reg_a };
    const char *why = NULL;
    printf("%u\n", (unsigned)imp_sum_irqs(&d1));
    /* Each call before the printf that shows its results: C leaves the order of arguments unspecified. */
    bool claimed = imp_claim(&d1, "fs", &why);
    printf("%d %s\n", claimed, why);
    claimed = imp_claim(&d2, "clock", &why);
    printf("%d [%s] %s %zu %d\n", claimed, why, d2.name, d2.users, d2.enabled);
    printf("%u\n", (unsigned)imp_sum_irqs(&d1));
    imp_link(&d1, &d3);
    printf("%d %llu\n", d1.next == &d3, (unsigned long long)d1.base);
    printf("%u\n", (unsigned)imp_sum_irqs(&d1));
    printf("%d %d\n", imp_same_owner(&d1, &d3), imp_same_owner(&d1, &d2));
    imp_point a = { 2, 4 }, b = { 6, -8 }, m = { 0, 0 };
    imp_mid(&a, &b, &m);
    printf("%d %d\n", m.x, m.y);
    printf("%u %u %u\n", d1.kind, d2.kind, d3.kind);
    printf("%d\n", imp_claim(&d3, "net", NULL));
    return 0;
}
