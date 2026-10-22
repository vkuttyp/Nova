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
