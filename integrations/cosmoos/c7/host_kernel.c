/*
 * host_kernel.c - C7's host stand-ins for the kernel functions the bindings call that CosmoOS's host harness does
 * not provide: kmalloc/kfree (over malloc, counting live blocks), the mutex API (single-threaded, keeping the kernel's
 * rule that sleeping with a spinlock held panics, as might_sleep() does), the module hooks kernel/object/object.c
 * calls (no modules on the host), quiescence on one CPU (a grace period is a synchronize call; callbacks run then, in
 * order), and the demo's counters. Spinlocks are CosmoOS's own host shim (tests/host/shim_spinlock.c), kobjects its
 * own kernel/object/object.c, and panic() its harness's.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <kernel/module.h>
#include <kernel/mutex.h>
#include <kernel/panic.h>
#include <kernel/quiesce.h>

#include "harness.h"

void *kmalloc(size_t size, unsigned flags);
void kfree(void *p);
uint32_t c7_locks_held(void);
unsigned c7_mutexes_held(void);
void c7_note_drop(uint32_t tag);
uint32_t c7_drops(void);
long c7_live_blocks(void);

static unsigned g_mutexes_held;
static long g_live_blocks;
static uint32_t g_drops;
static struct quiesce_head *g_pending;

void *kmalloc(size_t size, unsigned flags)
{
    void *p = malloc(size == 0 ? 1 : size);
    if (p != NULL && (flags & 1u))
        memset(p, 0, size);
    if (p != NULL)
        g_live_blocks++;
    return p;
}

void kfree(void *p)
{
    if (p != NULL)
        g_live_blocks--;
    free(p);
}

/* No modules on the host: every object's release is the kernel's own. */
struct module *module_owner_of(uintptr_t addr)
{
    (void)addr;
    return NULL;
}

void module_object_released(struct module *m)
{
    (void)m;
}

/* One CPU: the callbacks submitted so far run at the next grace period, oldest first. */
void call_quiesce(struct quiesce_head *h, void (*fn)(struct quiesce_head *h))
{
    if (h->pending)
        panic("call_quiesce: head %p submitted twice", (void *)h);
    h->pending = true;
    h->fn = fn;
    h->next = NULL;
    struct quiesce_head **tail = &g_pending;
    while (*tail != NULL)
        tail = &(*tail)->next;
    *tail = h;
}

void synchronize_quiesce(void)
{
    if (harness_locks_held() != 0) /* the kernel asserts this too */
        panic("synchronize_quiesce with %u spinlock(s) held", harness_locks_held());
    while (g_pending != NULL) {
        struct quiesce_head *h = g_pending;
        g_pending = h->next;
        h->pending = false;
        h->fn(h); /* may free h */
    }
}

void c7_note_drop(uint32_t tag)
{
    (void)tag;
    g_drops++;
}

uint32_t c7_drops(void)
{
    return g_drops;
}

long c7_live_blocks(void)
{
    return g_live_blocks;
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
