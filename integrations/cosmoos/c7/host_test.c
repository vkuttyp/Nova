/*
 * host_test.c - C7's host test: the Nova bindings (c7/sync.nova, kobject.nova, quiesce.nova) through the demo's exports (c7/demo.nova),
 * linked with CosmoOS's own host spinlock shim and harness, built with CosmoOS's HOST_CFLAGS (ASan, UBSan,
 * -Werror) by c7.py.
 */
#include "harness.h"

#include <stdint.h>

uint64_t c7_spin(uint64_t n);
uint32_t c7_irq_nested(void);
uint64_t c7_mutex(uint64_t n);
unsigned c7_mutexes_held(void);
uint32_t c7_kref(void);
uint32_t c7_defer(void);
uint32_t c7_drops(void);
long c7_live_blocks(void);

/* 100 hits, each seeing one lock held, and none held afterwards. */
static void test_spin(void)
{
    EXPECT(c7_spin(100) == 100 * 1000 + 100 + 1000000);
    EXPECT(harness_locks_held() == 0);
}

/* Two irqsave locks held at once, released in reverse order. */
static void test_irq_nested(void)
{
    EXPECT(c7_irq_nested() == 2 * 10 + 3);
    EXPECT(harness_locks_held() == 0);
}

/* A spinlock taken under a mutex, 50 times; the mutex sleeps only with no spinlock held. */
static void test_mutex(void)
{
    EXPECT(c7_mutex(50) == 50 * 1000 + 100);
    EXPECT(harness_locks_held() == 0);
    EXPECT(c7_mutexes_held() == 0);
}

/* Four references, three dropped again, the data kept; the last put on return releases the object, once. */
static void test_kref(void)
{
    uint32_t drops = c7_drops();
    EXPECT(c7_kref() == 7 * 100 + 4 * 10 + 1);
    EXPECT(c7_drops() == drops + 1);
    EXPECT(c7_live_blocks() == 0);
}

/* Still there after deferDrop, freed by the grace period, once. */
static void test_defer(void)
{
    EXPECT(c7_defer() == 9 * 100 + 0 * 10 + 1);
    EXPECT(c7_live_blocks() == 0);
}

/* Every lock and object made by the tests is freed. */
static void test_nothing_live(void)
{
    EXPECT(c7_live_blocks() == 0);
    EXPECT(harness_locks_held() == 0);
}

static const struct host_test tests[] = {
    { "c7-spin", test_spin },
    { "c7-irq-nested", test_irq_nested },
    { "c7-mutex", test_mutex },
    { "c7-kref", test_kref },
    { "c7-defer", test_defer },
    { "c7-nothing-live", test_nothing_live },
};

int main(void)
{
    return harness_run(tests, sizeof(tests) / sizeof(tests[0]));
}
