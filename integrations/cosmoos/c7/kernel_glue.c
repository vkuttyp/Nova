/*
 * kernel_glue.c - C7c's kernel side of the demo: c7_locks_held() from the kernel's own state. Every spinlock
 * acquisition disables preemption first (kernel/core/spinlock.c, lock_common), so in thread context with nothing
 * else disabling it, the CPU's preempt_count is the number of spinlocks held. raw_this_cpu() inlines to the
 * exported arch_percpu_get (kernel/arch/<arch>/percpu.c). Also the demo's drop counter.
 */
#include <kernel/percpu.h>

uint32_t c7_locks_held(void);

uint32_t c7_locks_held(void)
{
    return (uint32_t)raw_this_cpu()->preempt_count;
}

/* The demo's drop counter (c7/demo.nova's Tracked): the test's own state, like c5/module.c's. */
static uint32_t g_drops;

void c7_note_drop(uint32_t tag);
uint32_t c7_drops(void);

void c7_note_drop(uint32_t tag)
{
    (void)tag;
    __atomic_add_fetch(&g_drops, 1u, __ATOMIC_RELAXED);
}

uint32_t c7_drops(void)
{
    return __atomic_load_n(&g_drops, __ATOMIC_RELAXED);
}
