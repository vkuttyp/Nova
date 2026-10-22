/*
 * Nova runtime: core interface (freestanding).
 *
 * Generated C includes only this header. It uses only C11 freestanding headers and defines the
 * checked operations whose semantics are specified in docs/type-system.md §6-7 and mirrored by
 * Nova.Compiler.Semantics.IntegerSemantics. Platform services (output, exit, abort) are declared
 * at the bottom and supplied by a profile runtime, e.g. runtime/hosted/nova_rt_hosted.c.
 *
 * Define NV_NO_BUILTIN_OVERFLOW to use the portable overflow checks instead of compiler builtins.
 * The tests exercise both.
 */
#ifndef NOVA_RT_H
#define NOVA_RT_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(__GNUC__) || defined(__clang__)
#define NV_UNUSED __attribute__((unused))
#define NV_COLD __attribute__((cold))
#else
#define NV_UNUSED
#define NV_COLD
#endif

#if !defined(NV_NO_BUILTIN_OVERFLOW) && (defined(__clang__) || (defined(__GNUC__) && __GNUC__ >= 5))
#define NV_HAVE_BUILTIN_OVERFLOW 1
#endif

/* ---- Panics ---------------------------------------------------------------------------- */

typedef enum nv_panic_kind {
    NV_PANIC_OVERFLOW = 1,
    NV_PANIC_DIVIDE_BY_ZERO = 2,
    NV_PANIC_SHIFT = 3,
    NV_PANIC_UNREACHABLE = 4
} nv_panic_kind;

/* A Nova source location; `file` points to a string in the generated program's file table. */
typedef struct nv_loc {
    const char *file;
    uint32_t line;
    uint32_t column;
} nv_loc;

#define NV_LOC(file, line, column) ((nv_loc){(file), (line), (column)})

/* Reports the panic and aborts; never returns (docs/ownership.md §0: panic = abort). */
NV_COLD _Noreturn void nova_panic(nv_panic_kind kind, nv_loc loc);

/* ---- Checked arithmetic: + - * ---------------------------------------------------------- */

#if defined(NV_HAVE_BUILTIN_OVERFLOW)

#define NV_ARITH(T, S)                                                                    \
    static inline T nv_add_##S(T a, T b, nv_loc loc) {                                    \
        T r;                                                                              \
        if (__builtin_add_overflow(a, b, &r)) nova_panic(NV_PANIC_OVERFLOW, loc);        \
        return r;                                                                         \
    }                                                                                     \
    static inline T nv_sub_##S(T a, T b, nv_loc loc) {                                    \
        T r;                                                                              \
        if (__builtin_sub_overflow(a, b, &r)) nova_panic(NV_PANIC_OVERFLOW, loc);        \
        return r;                                                                         \
    }                                                                                     \
    static inline T nv_mul_##S(T a, T b, nv_loc loc) {                                    \
        T r;                                                                              \
        if (__builtin_mul_overflow(a, b, &r)) nova_panic(NV_PANIC_OVERFLOW, loc);        \
        return r;                                                                         \
    }
#define NV_ARITH_SIGNED_NARROW(T, S, MIN, MAX) NV_ARITH(T, S)
#define NV_ARITH_UNSIGNED_NARROW(T, S, MAX) NV_ARITH(T, S)
#define NV_ARITH_I64 NV_ARITH(int64_t, i64)
#define NV_ARITH_U64 NV_ARITH(uint64_t, u64)

#else /* portable fallback */

/* i8/i16/i32: the exact result always fits in int64_t. */
#define NV_ARITH_SIGNED_NARROW(T, S, MIN, MAX)                                            \
    static inline T nv_narrow_##S(int64_t r, nv_loc loc) {                                \
        if (r < (MIN) || r > (MAX)) nova_panic(NV_PANIC_OVERFLOW, loc);                   \
        return (T)r;                                                                      \
    }                                                                                     \
    static inline T nv_add_##S(T a, T b, nv_loc loc) { return nv_narrow_##S((int64_t)a + (int64_t)b, loc); } \
    static inline T nv_sub_##S(T a, T b, nv_loc loc) { return nv_narrow_##S((int64_t)a - (int64_t)b, loc); } \
    static inline T nv_mul_##S(T a, T b, nv_loc loc) { return nv_narrow_##S((int64_t)a * (int64_t)b, loc); }

/* u8/u16/u32: sums and products always fit in uint64_t; differences are checked first. */
#define NV_ARITH_UNSIGNED_NARROW(T, S, MAX)                                               \
    static inline T nv_narrow_##S(uint64_t r, nv_loc loc) {                               \
        if (r > (MAX)) nova_panic(NV_PANIC_OVERFLOW, loc);                                \
        return (T)r;                                                                      \
    }                                                                                     \
    static inline T nv_add_##S(T a, T b, nv_loc loc) { return nv_narrow_##S((uint64_t)a + (uint64_t)b, loc); } \
    static inline T nv_sub_##S(T a, T b, nv_loc loc) {                                    \
        if (a < b) nova_panic(NV_PANIC_OVERFLOW, loc);                                    \
        return (T)(a - b);                                                                \
    }                                                                                     \
    static inline T nv_mul_##S(T a, T b, nv_loc loc) { return nv_narrow_##S((uint64_t)a * (uint64_t)b, loc); }

/* i64: pre-checks in the style of CERT INT32-C; no operation overflows before the check. */
#define NV_ARITH_I64                                                                      \
    static inline int64_t nv_add_i64(int64_t a, int64_t b, nv_loc loc) {                  \
        if ((b > 0 && a > INT64_MAX - b) || (b < 0 && a < INT64_MIN - b))                 \
            nova_panic(NV_PANIC_OVERFLOW, loc);                                           \
        return a + b;                                                                     \
    }                                                                                     \
    static inline int64_t nv_sub_i64(int64_t a, int64_t b, nv_loc loc) {                  \
        if ((b < 0 && a > INT64_MAX + b) || (b > 0 && a < INT64_MIN + b))                 \
            nova_panic(NV_PANIC_OVERFLOW, loc);                                           \
        return a - b;                                                                     \
    }                                                                                     \
    static inline int64_t nv_mul_i64(int64_t a, int64_t b, nv_loc loc) {                  \
        if (a > 0) {                                                                      \
            if (b > 0 ? a > INT64_MAX / b : b < INT64_MIN / a)                            \
                nova_panic(NV_PANIC_OVERFLOW, loc);                                       \
        } else if (a < 0) {                                                               \
            if (b > 0 ? a < INT64_MIN / b : (b < 0 && a < INT64_MAX / b))                 \
                nova_panic(NV_PANIC_OVERFLOW, loc);                                       \
        }                                                                                 \
        return a * b;                                                                     \
    }

#define NV_ARITH_U64                                                                      \
    static inline uint64_t nv_add_u64(uint64_t a, uint64_t b, nv_loc loc) {               \
        if (a > UINT64_MAX - b) nova_panic(NV_PANIC_OVERFLOW, loc);                       \
        return a + b;                                                                     \
    }                                                                                     \
    static inline uint64_t nv_sub_u64(uint64_t a, uint64_t b, nv_loc loc) {               \
        if (a < b) nova_panic(NV_PANIC_OVERFLOW, loc);                                    \
        return a - b;                                                                     \
    }                                                                                     \
    static inline uint64_t nv_mul_u64(uint64_t a, uint64_t b, nv_loc loc) {               \
        if (a != 0 && b > UINT64_MAX / a) nova_panic(NV_PANIC_OVERFLOW, loc);             \
        return a * b;                                                                     \
    }

#endif

NV_ARITH_SIGNED_NARROW(int8_t, i8, INT8_MIN, INT8_MAX)
NV_ARITH_SIGNED_NARROW(int16_t, i16, INT16_MIN, INT16_MAX)
NV_ARITH_SIGNED_NARROW(int32_t, i32, INT32_MIN, INT32_MAX)
NV_ARITH_I64
NV_ARITH_UNSIGNED_NARROW(uint8_t, u8, UINT8_MAX)
NV_ARITH_UNSIGNED_NARROW(uint16_t, u16, UINT16_MAX)
NV_ARITH_UNSIGNED_NARROW(uint32_t, u32, UINT32_MAX)
NV_ARITH_U64

/* ---- Division, remainder, negation ------------------------------------------------------ */

/* `/` truncates toward zero; `%` has the dividend's sign; MIN / -1 overflows; MIN % -1 == 0. */
#define NV_DIV_SIGNED(T, S, MIN)                                                          \
    static inline T nv_div_##S(T a, T b, nv_loc loc) {                                    \
        if (b == 0) nova_panic(NV_PANIC_DIVIDE_BY_ZERO, loc);                             \
        if (a == (MIN) && b == -1) nova_panic(NV_PANIC_OVERFLOW, loc);                    \
        return (T)(a / b);                                                                \
    }                                                                                     \
    static inline T nv_rem_##S(T a, T b, nv_loc loc) {                                    \
        if (b == 0) nova_panic(NV_PANIC_DIVIDE_BY_ZERO, loc);                             \
        if (b == -1) return 0; /* avoids the undefined MIN % -1 in C */                   \
        return (T)(a % b);                                                                \
    }                                                                                     \
    static inline T nv_neg_##S(T a, nv_loc loc) {                                         \
        if (a == (MIN)) nova_panic(NV_PANIC_OVERFLOW, loc);                               \
        return (T)(-a);                                                                   \
    }

#define NV_DIV_UNSIGNED(T, S)                                                             \
    static inline T nv_div_##S(T a, T b, nv_loc loc) {                                    \
        if (b == 0) nova_panic(NV_PANIC_DIVIDE_BY_ZERO, loc);                             \
        return (T)(a / b);                                                                \
    }                                                                                     \
    static inline T nv_rem_##S(T a, T b, nv_loc loc) {                                    \
        if (b == 0) nova_panic(NV_PANIC_DIVIDE_BY_ZERO, loc);                             \
        return (T)(a % b);                                                                \
    }

NV_DIV_SIGNED(int8_t, i8, INT8_MIN)
NV_DIV_SIGNED(int16_t, i16, INT16_MIN)
NV_DIV_SIGNED(int32_t, i32, INT32_MIN)
NV_DIV_SIGNED(int64_t, i64, INT64_MIN)
NV_DIV_UNSIGNED(uint8_t, u8)
NV_DIV_UNSIGNED(uint16_t, u16)
NV_DIV_UNSIGNED(uint32_t, u32)
NV_DIV_UNSIGNED(uint64_t, u64)

/* ---- Shifts (left operand unsigned in M1; '>>' is logical) ------------------------------- */

static inline uint64_t nv_shift_amount_u(uint64_t n, uint64_t bits, nv_loc loc) {
    if (n >= bits) nova_panic(NV_PANIC_SHIFT, loc);
    return n;
}

static inline uint64_t nv_shift_amount_s(int64_t n, uint64_t bits, nv_loc loc) {
    if (n < 0 || (uint64_t)n >= bits) nova_panic(NV_PANIC_SHIFT, loc);
    return (uint64_t)n;
}

/* `n` has been validated by nv_shift_amount_*; bits shifted out are discarded. */
#define NV_SHIFT(T, S)                                                                    \
    static inline T nv_shl_##S(T a, uint64_t n) { return (T)((uint64_t)a << n); }         \
    static inline T nv_shr_##S(T a, uint64_t n) { return (T)((uint64_t)a >> n); }

NV_SHIFT(uint8_t, u8)
NV_SHIFT(uint16_t, u16)
NV_SHIFT(uint32_t, u32)
NV_SHIFT(uint64_t, u64)

/* ---- Reinterpretation (truncate<iN>) ----------------------------------------------------- */

/* Converting an out-of-range value to a signed type is implementation-defined in C11
 * (6.3.1.3p3), so two's-complement reinterpretation is spelled out. */
static inline int8_t nv_as_i8(uint8_t u) { return u <= INT8_MAX ? (int8_t)u : (int8_t)(-(int)(UINT8_MAX - u) - 1); }
static inline int16_t nv_as_i16(uint16_t u) { return u <= INT16_MAX ? (int16_t)u : (int16_t)(-(int)(UINT16_MAX - u) - 1); }
static inline int32_t nv_as_i32(uint32_t u) { return u <= INT32_MAX ? (int32_t)u : (int32_t)(-(int64_t)(UINT32_MAX - u) - 1); }
static inline int64_t nv_as_i64(uint64_t u) { return u <= INT64_MAX ? (int64_t)u : -(int64_t)(UINT64_MAX - u) - 1; }

/* ---- Platform services (supplied by the profile runtime) -------------------------------- */

void nova_platform_write_stdout(const uint8_t *bytes, size_t length);
void nova_platform_write_stderr(const uint8_t *bytes, size_t length);
_Noreturn void nova_platform_exit(int32_t code);
_Noreturn void nova_platform_abort(void);

/* ---- Output and process control (runtime/core/nova_rt_core.c) --------------------------- */

void nova_write_i64(int64_t value);
void nova_write_u64(uint64_t value);
void nova_write_bool(bool value);
void nova_write_str(const char *text, size_t length);
void nova_write_newline(void);
_Noreturn void nova_exit(int32_t code);

#endif /* NOVA_RT_H */
