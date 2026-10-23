/*
 * Nova runtime platform layer for hosted C environments (Linux, macOS, CosmoOS userland libc).
 * Uses only standard C library functions; Windows (MSVC, best effort) also uses its C runtime's
 * binary-mode and _exit calls (docs/c-backend.md §1.1).
 */
#if defined(_WIN32) && !defined(_CRT_SECURE_NO_WARNINGS)
#define _CRT_SECURE_NO_WARNINGS /* getenv is not deprecated in standard C */
#endif
#include "nova/rt.h"

#include <stdio.h>
#include <stdlib.h>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

/* Windows translates "\n" to "\r\n" in text mode: Nova's output is bytes, so both streams become binary. */
static void nova_binary_streams(void) {
#ifdef _WIN32
    static bool done;
    if (!done) {
        done = true;
        (void)_setmode(_fileno(stdout), _O_BINARY);
        (void)_setmode(_fileno(stderr), _O_BINARY);
    }
#endif
}

void nova_platform_write_stdout(const uint8_t *bytes, size_t length) {
    nova_binary_streams();
    (void)fwrite(bytes, 1u, length, stdout);
}

void nova_platform_write_stderr(const uint8_t *bytes, size_t length) {
    nova_binary_streams();
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
#ifdef _WIN32
    _exit(134); /* the status abort() gives on Linux and macOS (128 + SIGABRT); Windows' abort() gives 3 */
#else
    abort();
#endif
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
