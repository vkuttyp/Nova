/*
 * diff_lz4.c - C6a's differential test: CosmoOS's lz4.c (renamed c_lz4_*) against Nova's port (lz4_*), on
 * the same inputs. Compression must give identical bytes and lengths; decompression of valid and of
 * corrupted streams must give identical results and identical output buffers. Deterministic: seeded xorshift.
 *
 * Usage: diff_lz4 [cases] [seed]
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <kernel/lz4.h>

size_t c_lz4_compress(const void *src, size_t slen, void *dst, size_t dcap, void *work);
size_t c_lz4_decompress(const void *src, size_t slen, void *dst, size_t dcap);

static uint64_t state;
static uint64_t next(void) {
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

enum { MAX = 9000 };
static uint8_t input[MAX], packed_c[MAX * 2], packed_n[MAX * 2], out_c[MAX * 2], out_n[MAX * 2];
static uint32_t work_c[LZ4_WORK_BYTES / 4], work_n[LZ4_WORK_BYTES / 4];

/* Inputs of the shapes a filesystem sees: random, small alphabets, runs, repeated records. */
static size_t make_input(void) {
    size_t len = (size_t)(next() % MAX);
    unsigned kind = (unsigned)(next() % 4);
    for (size_t i = 0; i < len; i++) {
        switch (kind) {
        case 0: input[i] = (uint8_t)next(); break;
        case 1: input[i] = (uint8_t)("etaoin shrdlu\n"[next() % 14]); break;
        case 2: input[i] = (uint8_t)(i / (1 + next() % 300 + 1) % 3); break;
        default: input[i] = (uint8_t)((i % 61) * 7 + (next() % 16 == 0 ? next() : 0)); break;
        }
    }
    return len;
}

int main(int argc, char **argv) {
    unsigned long cases = argc > 1 ? strtoul(argv[1], NULL, 10) : 20000;
    state = argc > 2 ? strtoull(argv[2], NULL, 10) : 1;
    unsigned long compressed = 0, rejected = 0, mismatches = 0;
    for (unsigned long n = 0; n < cases; n++) {
        size_t len = make_input();
        size_t cap = (n % 7 == 0) ? (size_t)(next() % (len + 1)) : lz4_compress_bound(len); /* sometimes too small */
        memset(packed_c, 0xAA, sizeof packed_c);
        memset(packed_n, 0xAA, sizeof packed_n);
        size_t pc = c_lz4_compress(input, len, packed_c, cap, work_c);
        size_t pn = lz4_compress(input, len, packed_n, cap, work_n);
        if (pc != pn || memcmp(packed_c, packed_n, sizeof packed_c) != 0) {
            printf("compress mismatch: case %lu, len %zu, cap %zu: C %zu, Nova %zu\n", n, len, cap, pc, pn);
            mismatches++;
            continue;
        }
        if (pc != 0) {
            compressed++;
        }
        /* Decompress the stream, then a corrupted copy, into buffers of several sizes. */
        for (int round = 0; round < 3; round++) {
            size_t slen = pc;
            if (round > 0 && slen > 0) {
                for (int f = 0; f < 1 + round * 2; f++) {
                    packed_c[next() % slen] ^= (uint8_t)(1u << (next() % 8));
                }
                if (round == 2) {
                    slen = (size_t)(next() % (slen + 1));
                }
                memcpy(packed_n, packed_c, sizeof packed_c);
            }
            size_t dcap = round == 1 ? (size_t)(next() % (len + 8)) : len;
            memset(out_c, 0x55, sizeof out_c);
            memset(out_n, 0x55, sizeof out_n);
            size_t dc = c_lz4_decompress(packed_c, slen, out_c, dcap);
            size_t dn = lz4_decompress(packed_n, slen, out_n, dcap);
            if (dc != dn || memcmp(out_c, out_n, sizeof out_c) != 0) {
                printf("decompress mismatch: case %lu, round %d, slen %zu, dcap %zu: C %zu, Nova %zu\n", n, round, slen, dcap, dc, dn);
                mismatches++;
            }
            if (dc == 0 && round > 0) {
                rejected++;
            }
            if (round == 0 && pc != 0 && (dc != len || memcmp(out_c, input, len) != 0)) {
                printf("round trip failed: case %lu\n", n);
                mismatches++;
            }
        }
    }
    printf("diff_lz4: %lu cases, %lu compressed, %lu corrupted streams rejected, %lu mismatches\n", cases, compressed, rejected, mismatches);
    return mismatches == 0 ? 0 : 1;
}
