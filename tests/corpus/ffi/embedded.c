/* The C half of ffi/embedded.nova: the operations on the C state that Nova embeds. */
#include "embedded.types.h"

static uint32_t g_inits;
static struct emb_list g_node;
static struct emb_dev g_dev = { 41, { 0, 0, "dev" }, { 0, 0 } };

void emb_list_init(struct emb_list *l) {
    l->next = l;
    l->prev = l;
}

bool emb_list_empty(const struct emb_list *l) {
    return l->next == l;
}

/* Every link of the ring points back to its neighbour: false if anything moved after it was linked. */
bool emb_list_valid(const struct emb_list *l) {
    const struct emb_list *p = l;
    do {
        if (p->next->prev != p)
            return false;
        p = p->next;
    } while (p != l);
    return true;
}

void emb_list_add(struct emb_list *head, struct emb_list *node) {
    node->next = head->next;
    node->prev = head;
    head->next->prev = node;
    head->next = node;
}

void emb_lock_init(emb_lock_t *l, const char *name) {
    l->locked = 0;
    l->order = ++g_inits;
    l->name = name;
}

struct emb_dev *emb_dev_get(void) {
    if (g_dev.link.next == 0)
        emb_list_init(&g_dev.link);
    if (emb_list_empty(&g_dev.link))
        emb_list_add(&g_dev.link, &g_node);
    return &g_dev;
}
