/*
 * Nova runtime core (freestanding): integer formatting, output and panic reporting, built
 * only on the platform services declared in nova/rt.h. No libc.
 */
#include "nova/rt.h"

static size_t format_u64(uint64_t value, uint8_t buffer[20]) {
    size_t start = 20;
    do {
        buffer[--start] = (uint8_t)('0' + (value % 10u));
        value /= 10u;
    } while (value != 0u);
    return start;
}

void nova_write_u64(uint64_t value) {
    uint8_t buffer[20];
    size_t start = format_u64(value, buffer);
    nova_platform_write_stdout(buffer + start, 20u - start);
}

void nova_write_i64(int64_t value) {
    if (value < 0) {
        static const uint8_t minus = '-';
        nova_platform_write_stdout(&minus, 1u);
        /* -(value + 1) + 1 avoids negating INT64_MIN. */
        nova_write_u64((uint64_t)(-(value + 1)) + 1u);
    } else {
        nova_write_u64((uint64_t)value);
    }
}

void nova_write_bool(bool value) {
    static const uint8_t yes[] = {'t', 'r', 'u', 'e'};
    static const uint8_t no[] = {'f', 'a', 'l', 's', 'e'};
    if (value) {
        nova_platform_write_stdout(yes, sizeof yes);
    } else {
        nova_platform_write_stdout(no, sizeof no);
    }
}

void nova_write_str(const char *text, size_t length) {
    nova_platform_write_stdout((const uint8_t *)text, length);
}

void nova_write_newline(void) {
    static const uint8_t newline = '\n';
    nova_platform_write_stdout(&newline, 1u);
}

_Noreturn void nova_exit(int32_t code) {
    nova_platform_exit(code);
}

static void write_err_cstr(const char *text) {
    size_t length = 0;
    while (text[length] != '\0') {
        length++;
    }
    nova_platform_write_stderr((const uint8_t *)text, length);
}

static void write_err_u32(uint32_t value) {
    uint8_t buffer[20];
    size_t start = format_u64(value, buffer);
    nova_platform_write_stderr(buffer + start, 20u - start);
}

/* Message format (relied on by run-fail tests): "nova panic: <reason> at <file>:<line>:<column>\n" */
_Noreturn void nova_panic(nv_panic_kind kind, nv_loc loc) {
    const char *reason;
    switch (kind) {
    case NV_PANIC_OVERFLOW: reason = "integer overflow"; break;
    case NV_PANIC_DIVIDE_BY_ZERO: reason = "division by zero"; break;
    case NV_PANIC_SHIFT: reason = "shift amount out of range"; break;
    case NV_PANIC_UNREACHABLE: reason = "entered unreachable code"; break;
    case NV_PANIC_INDEX: reason = "index out of bounds"; break;
    case NV_PANIC_SLICE: reason = "slice range out of bounds"; break;
    case NV_PANIC_UNWRAP: reason = "unwrapped an Err value"; break;
    default: reason = "unknown panic"; break;
    }
    write_err_cstr("nova panic: ");
    write_err_cstr(reason);
    write_err_cstr(" at ");
    write_err_cstr(loc.file);
    write_err_cstr(":");
    write_err_u32(loc.line);
    write_err_cstr(":");
    write_err_u32(loc.column);
    write_err_cstr("\n");
    nova_platform_abort();
}

/* ---- Heap: accounting and fault injection (type-system.md §18) ---------------------------- */

static uint64_t alloc_attempts;   /* every allocation attempt, failed ones included */
static uint64_t alloc_live;       /* successful allocations not yet freed */
static uint64_t alloc_fail_at;    /* NOVA_FAIL_ALLOC_AT: this attempt fails; 0 = never */
static bool alloc_report;         /* NOVA_ALLOC_REPORT: print the counters at the end of main */
static bool failing_armed;        /* FailingAllocator.failAfter has been called */
static uint64_t failing_left;     /* successes FailingAllocator still allows */

void nova_rt_start(void) {
    alloc_fail_at = nova_platform_config("NOVA_FAIL_ALLOC_AT");
    alloc_report = nova_platform_config("NOVA_ALLOC_REPORT") != 0u;
}

void *nova_alloc(size_t size, uint32_t allocator) {
    alloc_attempts++;
    if (allocator == NV_ALLOC_FAILING && failing_armed) {
        if (failing_left == 0u) {
            return NULL;
        }
        failing_left--;
    }
    if (alloc_fail_at != 0u && alloc_attempts == alloc_fail_at) {
        return NULL;
    }
    void *p = nova_platform_alloc(size == 0u ? 1u : size);
    if (p != NULL) {
        alloc_live++;
    }
    return p;
}

void nova_free(void *p, uint32_t allocator) {
    (void)allocator; /* every M3 allocator frees to the platform heap */
    alloc_live--;
    nova_platform_free(p);
}

void nova_fail_after(uint64_t n) {
    failing_armed = true;
    failing_left = n;
}

void nova_fail_reset(void) {
    failing_armed = false;
}

static void write_err_u64(uint64_t value) {
    uint8_t buffer[20];
    size_t start = format_u64(value, buffer);
    nova_platform_write_stderr(buffer + start, 20u - start);
}

/* Format (relied on by the fault-injection sweep): "nova alloc: attempts=<n> live=<m>\n" */
int32_t nova_rt_finish(int32_t code) {
    if (alloc_report) {
        write_err_cstr("nova alloc: attempts=");
        write_err_u64(alloc_attempts);
        write_err_cstr(" live=");
        write_err_u64(alloc_live);
        write_err_cstr("\n");
    }
    return code;
}
