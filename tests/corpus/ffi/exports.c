/* The C half of ffi/exports.nova: calls Nova's exports through the generated header. */
#include "nova_exports.h"

int32_t c_entry(int32_t seed);
int64_t c_total(struct Point const *points, size_t count);
Color c_bad_color(void);
void c_fill(struct Segment *out, char const *name);

int32_t c_entry(int32_t seed) {
    struct Point p = { -3, seed };
    uint8_t bytes[3] = { 1, 2, 250 };
    Color c = geo_next_color(Color_Blue);
    int32_t flipped = geo_flip(Mode_Off);
    return geo_length(p) + (int32_t)geo_sum_bytes(bytes, 3) + (int32_t)c * 100 + flipped * 1000
        + (int32_t)geo_flag(false, 0);
}

int64_t c_total(struct Point const *points, size_t count) {
    int64_t total = 0;
    for (size_t i = 0; i < count; i++) {
        total += geo_length(points[i]);
    }
    return total * 2 + geo_flag(true, 4) - 8 + 10;
}

void c_fill(struct Segment *out, char const *name) {
    out->label = (char *)(void *)(uintptr_t)name;
    size_t n = 0;
    while (name[n] != 0) {
        n++;
    }
    out->length = n;
    out->from.x = 9;
}

Color c_bad_color(void) {
    return (Color)7;
}
