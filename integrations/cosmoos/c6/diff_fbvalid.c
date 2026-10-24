/*
 * diff_fbvalid.c - C6b's differential test: CosmoOS's fbvalid.c (renamed c_bootinfo_fb_validate) against Nova's
 * port (bootinfo_fb_validate), on the same framebuffer descriptions. Both must give the same answer, the same
 * reason (the same text), and on acceptance the same copied-out description. Deterministic: seeded xorshift.
 *
 * Descriptions come from three sources, so that every check is reached often: fully random fields; fields
 * drawn from each check's boundary values; and one valid mode with one field changed. One in 16 is then made
 * absent (all four location fields zero). The counts of each answer are printed, to show every check is reached.
 *
 * Usage: diff_fbvalid CASES SEED
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <kernel/bootinfo.h>

bool c_bootinfo_fb_validate(const struct cosmoboot_info *info, struct bootinfo_framebuffer *out, const char **why);

static uint64_t state;

static uint64_t next(void)
{
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

static uint64_t pick(const uint64_t *values, size_t count)
{
    return values[next() % count];
}

/* Values at each check's edges, for any field. */
static const uint64_t edges64[] = { 0, 1, 0x80000000ull, 0xfffff000ull, 0xffffffffull, 0x100000000ull,
                                    0xfffffffffffff000ull, 0xffffffffffffffffull, 5120ull * 800, 5184ull * 800 };
static const uint64_t edges32[] = { 0, 1, 2, 7, 8, 15, 16, 23, 24, 31, 32, 33, 64, 800, 1279, 1280, 5119, 5120,
                                    5184, 0x7fffffff, 0xfffffffe, 0xffffffff };
static const uint64_t edges8[] = { 0, 1, 7, 8, 9, 15, 16, 17, 24, 25, 31, 32, 33, 128, 255 };

static void good(struct cosmoboot_info *i)
{
    i->fb_phys = 0x80000000ull;
    i->fb_width = 1280;
    i->fb_height = 800;
    i->fb_bpp = 32;
    i->fb_pitch = 1280 * 4;
    i->fb_size = (uint64_t)i->fb_pitch * i->fb_height;
    i->fb_red_shift = 16;
    i->fb_red_bits = 8;
    i->fb_green_shift = 8;
    i->fb_green_bits = 8;
    i->fb_blue_shift = 0;
    i->fb_blue_bits = 8;
}

static void make(struct cosmoboot_info *i, unsigned kind)
{
    memset(i, 0, sizeof(*i));
    switch (kind) {
    case 0: /* random */
        i->fb_phys = next();
        i->fb_size = next() >> (next() % 64);
        i->fb_width = (uint32_t)next() >> (next() % 32);
        i->fb_height = (uint32_t)next() >> (next() % 32);
        i->fb_pitch = (uint32_t)next() >> (next() % 32);
        i->fb_bpp = (uint32_t)next() % 40;
        i->fb_red_shift = (uint8_t)next();
        i->fb_red_bits = (uint8_t)next();
        i->fb_green_shift = (uint8_t)next();
        i->fb_green_bits = (uint8_t)next();
        i->fb_blue_shift = (uint8_t)next();
        i->fb_blue_bits = (uint8_t)next();
        break;
    case 1: /* boundary values */
        i->fb_phys = pick(edges64, sizeof edges64 / sizeof edges64[0]);
        i->fb_size = pick(edges64, sizeof edges64 / sizeof edges64[0]);
        i->fb_width = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]);
        i->fb_height = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]);
        i->fb_pitch = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]);
        i->fb_bpp = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]);
        i->fb_red_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        i->fb_red_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        i->fb_green_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        i->fb_green_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        i->fb_blue_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        i->fb_blue_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]);
        break;
    default: /* a valid mode with one field changed */
        good(i);
        switch (next() % 12) {
        case 0: i->fb_phys = pick(edges64, sizeof edges64 / sizeof edges64[0]); break;
        case 1: i->fb_size = pick(edges64, sizeof edges64 / sizeof edges64[0]); break;
        case 2: i->fb_width = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]); break;
        case 3: i->fb_height = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]); break;
        case 4: i->fb_pitch = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]); break;
        case 5: i->fb_bpp = (uint32_t)pick(edges32, sizeof edges32 / sizeof edges32[0]); break;
        case 6: i->fb_red_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        case 7: i->fb_red_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        case 8: i->fb_green_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        case 9: i->fb_green_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        case 10: i->fb_blue_shift = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        default: i->fb_blue_bits = (uint8_t)pick(edges8, sizeof edges8 / sizeof edges8[0]); break;
        }
        break;
    }
    /* Absent (the four location fields zero) is its own answer. */
    if (next() % 16 == 0)
        i->fb_phys = i->fb_size = i->fb_width = i->fb_height = 0;
}

enum { MAX_REASONS = 32 };
static const char *reasons[MAX_REASONS];
static unsigned long reason_counts[MAX_REASONS];

static void count(const char *why)
{
    for (unsigned i = 0; i < MAX_REASONS; i++) {
        if (reasons[i] == NULL)
            reasons[i] = why;
        if (strcmp(reasons[i], why) == 0) {
            reason_counts[i]++;
            return;
        }
    }
}

int main(int argc, char **argv)
{
    unsigned long cases = argc > 1 ? strtoul(argv[1], NULL, 10) : 100000;
    state = argc > 2 ? strtoull(argv[2], NULL, 10) : 1;
    if (state == 0)
        state = 1;
    unsigned long mismatches = 0, accepted = 0;
    for (unsigned long n = 0; n < cases; n++) {
        struct cosmoboot_info info;
        make(&info, (unsigned)(n % 3));
        /* Every eighth case leaves out one of the pointers (each may be NULL). */
        unsigned nulls = n % 8 == 7 ? (unsigned)(next() % 3) + 1 : 0;
        struct bootinfo_framebuffer a, b;
        memset(&a, 0xa5, sizeof a);
        memset(&b, 0xa5, sizeof b);
        const char *why_a = NULL, *why_b = NULL;
        const struct cosmoboot_info *in = nulls == 1 ? NULL : &info;
        bool rc_a = c_bootinfo_fb_validate(in, nulls == 2 ? NULL : &a, nulls == 3 ? NULL : &why_a);
        bool rc_b = bootinfo_fb_validate(in, nulls == 2 ? NULL : &b, nulls == 3 ? NULL : &why_b);
        bool same = rc_a == rc_b && memcmp(&a, &b, sizeof a) == 0
                    && (nulls == 3 ? why_a == NULL && why_b == NULL : why_a != NULL && why_b != NULL && strcmp(why_a, why_b) == 0);
        if (!same) {
            if (mismatches++ < 5)
                printf("mismatch at case %lu: C %d \"%s\", Nova %d \"%s\"\n", n, rc_a, why_a ? why_a : "(null)", rc_b,
                       why_b ? why_b : "(null)");
            continue;
        }
        if (rc_a)
            accepted++;
        if (rc_a || why_a != NULL)
            count(rc_a ? "(accepted)" : why_a);
    }
    unsigned distinct = 0;
    for (unsigned i = 0; i < MAX_REASONS && reasons[i] != NULL; i++) {
        printf("  %8lu  %s\n", reason_counts[i], reasons[i]);
        distinct++;
    }
    printf("diff_fbvalid: %lu cases, %lu accepted, %u distinct answers, %lu mismatches\n", cases, accepted, distinct, mismatches);
    return mismatches == 0 ? 0 : 1;
}
