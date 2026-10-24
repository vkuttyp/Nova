/*
 * module.c - C5's test module: runs Nova's in-kernel selftest (selftest.nova) from init and logs each result.
 * The fault-injection point and the live-block count for TestHeap live here, on the C side, because a Nova
 * allocator has no fields (type-system.md §30.2). Built out of tree by integrations/cosmoos/c5.py with CosmoOS's
 * own MODULE_CFLAGS, joined and signed by its own tools; CosmoOS is not modified.
 */
#include <kernel/errno.h>
#include <kernel/log.h>
#include <kernel/module.h>

#include "nova_exports.h"

void c5_report(const char *name, bool passed, int64_t detail);
bool c5_should_fail(void);
void c5_count_alloc(void);
void c5_count_free(void);
int64_t c5_live(void);
void c5_fail_at(uint64_t n);

static uint64_t c5_attempts;   /* TestHeap allocations since the last c5_fail_at */
static uint64_t c5_failing;    /* fail this attempt (1-based); 0: never */
static int64_t c5_blocks;      /* TestHeap blocks allocated and not yet freed */

void c5_report(const char *name, bool passed, int64_t detail)
{
    kinfo("nova_c5: %s %s (%lld)", name, passed ? "PASS" : "FAIL", (long long)detail);
}

bool c5_should_fail(void)
{
    c5_attempts++;
    return c5_failing != 0 && c5_attempts == c5_failing;
}

void c5_count_alloc(void)
{
    c5_blocks++;
}

void c5_count_free(void)
{
    c5_blocks--;
}

int64_t c5_live(void)
{
    return c5_blocks;
}

void c5_fail_at(uint64_t n)
{
    c5_failing = n;
    c5_attempts = 0;
}

static int c5_init(void)
{
    uint32_t failed = c5_selftest();
    if (failed != 0) {
        kerror("nova_c5: %u test(s) failed", (unsigned)failed);
        return -EINVAL;
    }
    kinfo("nova_c5: all tests passed; %lld TestHeap block(s) live", (long long)c5_blocks);
    return 0;
}

static void c5_shutdown(void)
{
    kinfo("nova_c5: shutdown");
}

COSMO_MODULE("nova_c5", "1.0", c5_init, c5_shutdown, "", MODULE_CAP_NONE);
