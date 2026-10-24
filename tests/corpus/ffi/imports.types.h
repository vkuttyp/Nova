/* The C types ffi/imports.nova imports (type-system.md §31). Nova declares only some members, in another order. */
#ifndef IMPORTS_TYPES_H
#define IMPORTS_TYPES_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

struct imp_registry; /* defined in imports.c only: opaque to Nova too */

struct imp_device {
    uint8_t kind; /* not declared in Nova: its writes must leave it alone */
    uint64_t base;
    uint32_t irq;
    bool enabled;
    const char *name;
    size_t users;
    struct imp_device *next;
    struct imp_registry *owner;
};

typedef struct {
    int32_t x, y;
} imp_point;

#endif
