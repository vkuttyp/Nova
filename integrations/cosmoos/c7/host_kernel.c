/*
 * host_kernel.c - C7c's host stand-ins for the kernel functions the lock bindings call that CosmoOS's host harness
 * does not provide: kmalloc/kfree (over malloc), the mutex API (single-threaded, keeping the kernel's rule that
 * sleeping with a spinlock held panics, as might_sleep() does), and c7_locks_held for the demo. Spinlocks are
 * CosmoOS's own host shim (tests/host/shim_spinlock.c), and panic() is its harness's.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <kernel/mutex.h>
#include <kernel/panic.h>

#include "harness.h"

void *kmalloc(size_t size, unsigned flags);
void kfree(void *p);
uint32_t c7_locks_held(void);
unsigned c7_mutexes_held(void);

static unsigned g_mutexes_held;

void *kmalloc(size_t size, unsigned flags)
{
    void *p = malloc(size == 0 ? 1 : size);
    if (p != NULL && (flags & 1u))
        memset(p, 0, size);
    return p;
}

void kfree(void *p)
{
    free(p);
}

void mutex_init(struct mutex *m, const char *name)
{
    memset(m, 0, sizeof(*m));
    m->name = name;
    list_init(&m->pi_link); /* as the kernel's: the mutex now points into itself */
}

void mutex_lock(struct mutex *m)
{
    if (harness_locks_held() != 0) /* the kernel's might_sleep() */
        panic("host mutex '%s': sleeping call with %u spinlock(s) held", m->name, harness_locks_held());
    if (m->pi_link.next != &m->pi_link)
        panic("host mutex '%s': used after it moved, or before mutex_init", m->name);
    if (m->owner != NULL)
        panic("host mutex '%s' re-acquired (deadlock)", m->name);
    m->owner = (struct thread *)m;
    g_mutexes_held++;
}

bool mutex_trylock(struct mutex *m)
{
    if (m->owner != NULL)
        return false;
    mutex_lock(m);
    return true;
}

void mutex_unlock(struct mutex *m)
{
    if (m->owner == NULL)
        panic("host mutex '%s' unlocked while free", m->name);
    m->owner = NULL;
    g_mutexes_held--;
}

uint32_t c7_locks_held(void)
{
    return harness_locks_held();
}

unsigned c7_mutexes_held(void)
{
    return g_mutexes_held;
}
