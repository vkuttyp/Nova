/*
 * Nova runtime platform layer for hosted C environments (Linux, macOS, CosmoOS userland libc).
 * Uses only standard C library functions.
 */
#include "nova/rt.h"

#include <stdio.h>
#include <stdlib.h>

void nova_platform_write_stdout(const uint8_t *bytes, size_t length) {
    (void)fwrite(bytes, 1u, length, stdout);
}

void nova_platform_write_stderr(const uint8_t *bytes, size_t length) {
    /* Flush stdout first so output printed before a panic appears before the message. */
    (void)fflush(stdout);
    (void)fwrite(bytes, 1u, length, stderr);
}

_Noreturn void nova_platform_exit(int32_t code) {
    exit((int)code); /* flushes stdio */
}

_Noreturn void nova_platform_abort(void) {
    (void)fflush(stdout);
    (void)fflush(stderr);
    abort();
}

void *nova_platform_alloc(size_t size) {
    return malloc(size); /* aligned for max_align_t */
}

void nova_platform_free(void *p) {
    free(p);
}

uint64_t nova_platform_config(const char *name) {
    const char *text = getenv(name);
    if (text == NULL) {
        return 0u;
    }
    uint64_t value = 0u;
    for (; *text >= '0' && *text <= '9'; text++) {
        value = value * 10u + (uint64_t)(*text - '0');
    }
    return value;
}
