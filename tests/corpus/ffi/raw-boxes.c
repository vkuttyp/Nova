/* The C half of ffi/raw-boxes.nova: holds Nova objects as void pointers with their release callbacks. */
#include <stdio.h>

#include <stdint.h>

typedef void (*rb_release_fn)(void *);

static void *g_objects[4];
static rb_release_fn g_releases[4];
static int g_count;

void rb_hold(void *object, rb_release_fn release);
void rb_release_all(void);
void rb_note(int32_t tag);

void rb_hold(void *object, rb_release_fn release) {
    g_objects[g_count] = object;
    g_releases[g_count] = release;
    g_count++;
}

/* Last in, first out, like a kernel dropping its references. */
void rb_release_all(void) {
    while (g_count > 0) {
        g_count--;
        g_releases[g_count](g_objects[g_count]);
    }
}

void rb_note(int32_t tag) {
    printf("dropped %d\n", tag);
}
