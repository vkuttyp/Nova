/* C types that ffi/embedded.nova embeds by value (type-system.md §33). */
#ifndef EMBEDDED_TYPES_H
#define EMBEDDED_TYPES_H

#include <stdbool.h>
#include <stdint.h>

/* A list head: once initialised it points to itself, so it must never move. */
struct emb_list {
    struct emb_list *next, *prev;
};

/* A "lock": its init records the order in which locks were initialised. */
typedef struct {
    uint32_t locked;
    uint32_t order;
    const char *name;
} emb_lock_t;

/* A C object with embedded members. */
struct emb_dev {
    uint32_t id;
    emb_lock_t lock;
    struct emb_list link;
};

void emb_list_init(struct emb_list *l);
bool emb_list_empty(const struct emb_list *l);
bool emb_list_valid(const struct emb_list *l);
void emb_list_add(struct emb_list *head, struct emb_list *node);
void emb_lock_init(emb_lock_t *l, const char *name);
struct emb_dev *emb_dev_get(void);

#endif
