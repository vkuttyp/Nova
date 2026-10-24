/*
 * bench.c - C6a: CosmoOS's LZ4 codec (kernel/core/lz4.c, renamed c_lz4_*) against Nova's port
 * (integrations/cosmoos/c6/lz4.nova, lz4_*), in one binary, on deterministic data. Run by run.py, which builds
 * both at -O2 with the same compiler and records the machine; see benchmarks/lz4/README.md.
 *
 * For each data set and codec: compress and decompress the data set's 4 KiB blocks (a filesystem record's
 * size) for a fixed number of rounds, and report the median of 7 timed repetitions as MB/s of input.
 */
#define _POSIX_C_SOURCE 199309L /* clock_gettime under -std=c11 (glibc) */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <kernel/lz4.h>

size_t c_lz4_compress(const void *src, size_t slen, void *dst, size_t dcap, void *work);
size_t c_lz4_decompress(const void *src, size_t slen, void *dst, size_t dcap);

enum { BLOCK = 4096, BLOCKS = 256, REPS = 7 };
static uint8_t data[BLOCKS][BLOCK], packed[BLOCKS][BLOCK + BLOCK / 255 + 16], out[BLOCK];
static size_t packed_len[BLOCKS];
static uint32_t work[LZ4_WORK_BYTES / 4];

typedef size_t (*compress_fn)(const void *, size_t, void *, size_t, void *);
typedef size_t (*decompress_fn)(const void *, size_t, void *, size_t);

static uint64_t state = 1;
static uint64_t next(void) {
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

static void fill(int kind) {
    static const char *words[] = {"inode ", "extent ", "block ", "record ", "journal ", "0x0000", "\n", "cosmofs "};
    for (int b = 0; b < BLOCKS; b++) {
        size_t i = 0;
        while (i < BLOCK) {
            if (kind == 0) { /* text-like: words from a small vocabulary */
                const char *w = words[next() % 8];
                for (size_t k = 0; w[k] && i < BLOCK; k++) data[b][i++] = (uint8_t)w[k];
            } else if (kind == 1) { /* incompressible */
                data[b][i++] = (uint8_t)next();
            } else { /* structured records with small variations */
                data[b][i] = (uint8_t)((i % 64) < 48 ? (i % 64) : next());
                i++;
            }
        }
    }
}

static double now(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
}

static int compare(const void *a, const void *b) {
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

static double median_mbps(double *seconds, double bytes) {
    qsort(seconds, REPS, sizeof seconds[0], compare);
    return bytes / seconds[REPS / 2] / 1e6;
}

static void measure(const char *set, const char *codec, compress_fn compress, decompress_fn decompress, int rounds) {
    double cs[REPS], ds[REPS];
    size_t total = 0;
    for (int r = 0; r < REPS; r++) {
        double t0 = now();
        for (int k = 0; k < rounds; k++)
            for (int b = 0; b < BLOCKS; b++) packed_len[b] = compress(data[b], BLOCK, packed[b], sizeof packed[b], work);
        cs[r] = now() - t0;
        t0 = now();
        for (int k = 0; k < rounds; k++)
            for (int b = 0; b < BLOCKS; b++) total += decompress(packed[b], packed_len[b], out, BLOCK);
        ds[r] = now() - t0;
    }
    size_t compressed = 0;
    for (int b = 0; b < BLOCKS; b++) compressed += packed_len[b];
    double bytes = (double)BLOCK * BLOCKS * rounds;
    printf("%-11s %-5s compress %8.1f MB/s  decompress %8.1f MB/s  ratio %.3f  (check %zu)\n", set, codec,
           median_mbps(cs, bytes), median_mbps(ds, bytes), (double)compressed / (BLOCK * BLOCKS), total % 1000);
}

int main(int argc, char **argv) {
    int rounds = argc > 1 ? atoi(argv[1]) : 20;
    static const char *sets[] = {"text", "random", "records"};
    for (int kind = 0; kind < 3; kind++) {
        state = 1;
        fill(kind);
        measure(sets[kind], "C", c_lz4_compress, c_lz4_decompress, rounds);
        measure(sets[kind], "Nova", lz4_compress, lz4_decompress, rounds);
    }
    return 0;
}
