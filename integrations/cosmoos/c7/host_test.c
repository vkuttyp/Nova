/*
 * host_test.c - C7c's host test: the Nova lock bindings (c7/sync.nova) through the demo's exports (c7/demo.nova),
 * linked with CosmoOS's own host spinlock shim and harness, built with CosmoOS's HOST_CFLAGS (ASan, UBSan,
 * -Werror) by c7.py.
 */
#include "harness.h"

#include <stdint.h>

uint64_t c7_spin(uint64_t n);
uint32_t c7_irq_nested(void);
uint64_t c7_mutex(uint64_t n);
unsigned c7_mutexes_held(void);

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

static const struct host_test tests[] = {
    { "c7-spin", test_spin },
    { "c7-irq-nested", test_irq_nested },
    { "c7-mutex", test_mutex },
};

int main(void)
{
    return harness_run(tests, sizeof(tests) / sizeof(tests[0]));
}
