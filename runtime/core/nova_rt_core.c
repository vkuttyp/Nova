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
