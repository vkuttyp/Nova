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
    NV_PANIC_UNREACHABLE = 4,
    NV_PANIC_INDEX = 5,
    NV_PANIC_SLICE = 6,
    NV_PANIC_UNWRAP = 7,
    NV_PANIC_SCRATCH = 8
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

/* ---- Bounds checks (type-system.md §16.3) ------------------------------------------------- */

static inline void nv_check_index(uint64_t index, uint64_t length, nv_loc loc) {
    if (index >= length) nova_panic(NV_PANIC_INDEX, loc);
}

static inline void nv_check_range(uint64_t low, uint64_t high, uint64_t length, nv_loc loc) {
    if (low > high || high > length) nova_panic(NV_PANIC_SLICE, loc);
}

/* ---- Platform services (supplied by the profile runtime) -------------------------------- */

void nova_platform_write_stdout(const uint8_t *bytes, size_t length);
void nova_platform_write_stderr(const uint8_t *bytes, size_t length);
_Noreturn void nova_platform_exit(int32_t code);
_Noreturn void nova_platform_abort(void);
/* Heap memory; NULL on failure. Must be aligned for any Nova type (max_align_t). */
void *nova_platform_alloc(size_t size);
void nova_platform_free(void *p);
/* A configuration number from the environment; 0 if absent or not a number (hosted test builds). */
uint64_t nova_platform_config(const char *name);

/* ---- Heap (type-system.md §18; runtime/core/nova_rt_core.c) ------------------------------ */

/* Allocator types, as passed by generated code. */
#define NV_ALLOC_SYSTEM 0u
#define NV_ALLOC_FAILING 1u

/*
 * Allocation for Box and every later heap type. Every attempt is counted, and the attempt numbered
 * NOVA_FAIL_ALLOC_AT (1-based, all allocators) fails: the fault-injection hook for the M3 sweep.
 */
void *nova_alloc(size_t size, uint32_t allocator);
void nova_free(void *p, uint32_t allocator);
/* FailingAllocator.failAfter(n) / FailingAllocator.reset(). */
void nova_fail_after(uint64_t n);
void nova_fail_reset(void);
/* ---- Arena (type-system.md §19.2; runtime/core/nova_rt_core.c) ---------------------------- */

/*
 * A bump allocator over a list of chunks from a free-standing allocator. Layout fixed by Nova's
 * TypeTable (five 8-byte fields: 40 bytes, alignment 8 on LP64).
 */
typedef struct nv_arena {
    void *chunks;         /* most recent chunk; each chunk starts with the previous chunk's address */
    uint8_t *cur;         /* next free byte in the current chunk */
    uint8_t *end;         /* end of the current chunk */
    uint64_t chunk_size;  /* bytes per chunk (larger allocations get their own) */
    uint64_t allocator;   /* NV_ALLOC_* */
} nv_arena;
#if UINTPTR_MAX == UINT64_MAX
_Static_assert(sizeof(nv_arena) == 40 && _Alignof(nv_arena) == 8, "nova/rt.h: nv_arena layout");
#endif

nv_arena nova_arena_create(uint32_t allocator, uint64_t chunk_size);
/* `size` bytes aligned to `align` (a power of two ≤ 16); NULL if no chunk can be obtained. */
void *nova_arena_alloc(nv_arena *a, uint64_t size, uint64_t align);
/* Releases every chunk; the arena stays usable. Drop = reset. */
void nova_arena_reset(nv_arena *a);
void nova_arena_drop(nv_arena *a);

/* ---- Fixed-buffer allocator and scratch frames (type-system.md §19.4-19.5) ---------------- */

/* Allocation from caller-provided bytes; nothing is freed. 24 bytes on LP64. */
typedef struct nv_fba {
    uint8_t *base;
    uint64_t capacity;
    uint64_t used;
} nv_fba;

/*
 * A scratch frame: the scratch stack's state when the frame was entered, which its drop restores.
 * The scratch stack itself (runtime-owned heap chunks) is global; v0 is single-threaded. 32 bytes.
 */
typedef struct nv_scratch {
    void *chunk;
    uint8_t *cur;
    uint8_t *end;
    uint64_t depth;
} nv_scratch;
#if UINTPTR_MAX == UINT64_MAX
_Static_assert(sizeof(nv_fba) == 24 && _Alignof(nv_fba) == 8, "nova/rt.h: nv_fba layout");
_Static_assert(sizeof(nv_scratch) == 32 && _Alignof(nv_scratch) == 8, "nova/rt.h: nv_scratch layout");
#endif

nv_fba nova_fba_over(uint8_t *base, uint64_t capacity);
/* Panics if a scratch frame is already active: nested frames come from nova_scratch_enter_child. */
nv_scratch nova_scratch_enter_root(nv_loc loc);
nv_scratch nova_scratch_enter_child(nv_scratch *parent);
void nova_scratch_drop(nv_scratch *frame);

/* Allocation from any region (the generated helpers' single entry point); NULL on failure. */
#define NV_REGION_ARENA 0u
#define NV_REGION_FBA 1u
#define NV_REGION_SCRATCH 2u
void *nova_region_alloc(void *region, uint32_t kind, uint64_t size, uint64_t align);

/* Called by the generated `main`: configuration at start; the NOVA_ALLOC_REPORT line at the end. */
void nova_rt_start(void);
int32_t nova_rt_finish(int32_t code);

/* ---- Output and process control (runtime/core/nova_rt_core.c) --------------------------- */

void nova_write_i64(int64_t value);
void nova_write_u64(uint64_t value);
void nova_write_bool(bool value);
void nova_write_str(const char *text, size_t length);
void nova_write_newline(void);
_Noreturn void nova_exit(int32_t code);

#endif /* NOVA_RT_H */
