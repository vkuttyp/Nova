/*
 * kernel_glue.c - C7c's kernel side of the demo: c7_locks_held() from the kernel's own state. Every spinlock
 * acquisition disables preemption first (kernel/core/spinlock.c, lock_common), so in thread context with nothing
 * else disabling it, the CPU's preempt_count is the number of spinlocks held. raw_this_cpu() inlines to the
 * exported arch_percpu_get (kernel/arch/<arch>/percpu.c).
 */
#include <kernel/percpu.h>

uint32_t c7_locks_held(void);

uint32_t c7_locks_held(void)
{
    return (uint32_t)raw_this_cpu()->preempt_count;
}
