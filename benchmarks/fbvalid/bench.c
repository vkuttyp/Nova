/*
 * bench.c - C6b: CosmoOS's framebuffer validator (kernel/core/fbvalid.c, renamed c_bootinfo_fb_validate) against
 * Nova's port (integrations/cosmoos/c6/fbvalid.nova, bootinfo_fb_validate), in one binary, on deterministic
 * descriptions. Run by run.py, which builds both at -O2 with the same compiler and records the machine; see
 * benchmarks/fbvalid/README.md.
 *
 * Each repetition validates 4096 descriptions (a quarter valid modes, the rest one field changed, which most
 * checks refuse) a fixed number of rounds; the figure is the median of 7 repetitions, in nanoseconds per call.
 */
#define _POSIX_C_SOURCE 199309L /* clock_gettime under -std=c11 (glibc) */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <kernel/bootinfo.h>

bool c_bootinfo_fb_validate(const struct cosmoboot_info *info, struct bootinfo_framebuffer *out, const char **why);

typedef bool (*validate_fn)(const struct cosmoboot_info *, struct bootinfo_framebuffer *, const char **);

enum { COUNT = 4096, REPS = 7 };
static struct cosmoboot_info infos[COUNT];

static uint64_t state = 1;
static uint64_t next(void)
{
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

static void fill(void)
{
    for (unsigned n = 0; n < COUNT; n++) {
        struct cosmoboot_info *i = &infos[n];
        memset(i, 0, sizeof(*i));
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
        i->fb_blue_bits = 8;
        if (n % 4 == 0)
            continue;
        switch (next() % 6) {
        case 0: i->fb_bpp = (uint32_t)(next() % 40); break;
        case 1: i->fb_pitch = (uint32_t)(next() % 8192); break;
        case 2: i->fb_size = next() % (1u << 23); break;
        case 3: i->fb_phys = next(); break;
        case 4: i->fb_blue_shift = (uint8_t)next(); break;
        default: i->fb_height = (uint32_t)next() >> (next() % 32); break;
        }
    }
}

static double now(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
}

static int compare(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

static void measure(const char *name, validate_fn validate, int rounds)
{
    double seconds[REPS];
    unsigned long accepted = 0, why_bytes = 0;
    for (int r = 0; r < REPS; r++) {
        double t0 = now();
        for (int k = 0; k < rounds; k++) {
            for (unsigned n = 0; n < COUNT; n++) {
                struct bootinfo_framebuffer fb;
                const char *why;
                accepted += validate(&infos[n], &fb, &why);
                why_bytes += (unsigned char)why[0];
            }
        }
        seconds[r] = now() - t0;
    }
    qsort(seconds, REPS, sizeof seconds[0], compare);
    double calls = (double)COUNT * rounds;
    /* Per pass over the set: equal for both validators, or they did not do the same work. */
    unsigned long passes = (unsigned long)REPS * (unsigned long)rounds;
    printf("%-5s %7.2f ns/call  (accepted %lu of %d, check %lu)\n", name, seconds[REPS / 2] / calls * 1e9, accepted / passes, COUNT,
           why_bytes / passes);
}

int main(int argc, char **argv)
{
    int rounds = argc > 1 ? atoi(argv[1]) : 2000;
    fill();
    measure("C", c_bootinfo_fb_validate, rounds);
    measure("Nova", bootinfo_fb_validate, rounds);
    return 0;
}
