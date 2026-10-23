/*
 * Nova runtime: program arguments, files and TCP sockets (type-system.md §26) for hosted C
 * environments (Linux, macOS) and CosmoOS userland. Only public C library functions (L1, OD-15).
 */
#include "nova/rt.h"

#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <unistd.h>

#ifndef COSMO_AF_INET
extern char **environ;
#endif

/* ---- Program arguments ------------------------------------------------------------------ */

static int32_t nova_argc;
static const uint8_t *const *nova_argv;

void nova_rt_args(int32_t count, const uint8_t *const *arguments) {
    nova_argc = count;
    nova_argv = arguments;
}

uint64_t nova_arg_count(void) {
    return nova_argc > 0 ? (uint64_t)nova_argc : 0u;
}

bool nova_arg(uint64_t index, nv_str *out) {
    if (index >= nova_arg_count()) return false;
    const uint8_t *text = nova_argv[index];
    uint64_t length = 0;
    while (text[length] != 0) length++;
    if (!nova_utf8_valid(text, length)) return false;
    *out = (nv_str){ .ptr = text, .len = length };
    return true;
}

/* ---- Errors ----------------------------------------------------------------------------- */

uint32_t nova_io_kind(int32_t error) {
    switch (error) {
    case ENOENT: return NV_IO_NOT_FOUND;
    case EACCES: case EPERM: return NV_IO_PERMISSION_DENIED;
    case EEXIST: return NV_IO_ALREADY_EXISTS;
    case EISDIR: return NV_IO_IS_DIRECTORY;
    case ENOTDIR: return NV_IO_NOT_DIRECTORY;
    case EINVAL: case ENAMETOOLONG: return NV_IO_INVALID_INPUT;
    case EPIPE: return NV_IO_BROKEN_PIPE;
    case ECONNREFUSED: return NV_IO_CONNECTION_REFUSED;
    case EADDRINUSE: return NV_IO_ADDRESS_IN_USE;
    case ENOMEM: return NV_IO_OUT_OF_MEMORY;
    default: return NV_IO_OTHER;
    }
}

static nv_io_result nova_io_ok(int64_t value) { return (nv_io_result){ .value = value, .error = 0 }; }

static nv_io_result nova_io_errno(void) {
    int32_t error = (int32_t)errno;
    return (nv_io_result){ .value = 0, .error = error != 0 ? error : EIO };
}

static nv_io_result nova_io_fail(int32_t error) { return (nv_io_result){ .value = 0, .error = error }; }

/* ---- Files ------------------------------------------------------------------------------ */

/* A NUL-terminated copy of `path` in `buffer`; false if it contains a zero byte or is too long. */
static bool nova_c_path(nv_str path, char buffer[4096]) {
    if (path.len >= 4096u) return false;
    for (uint64_t i = 0; i < path.len; i++) {
        if (path.ptr[i] == 0) return false;
        buffer[i] = (char)path.ptr[i];
    }
    buffer[path.len] = 0;
    return true;
}

nv_io_result nova_file_open(nv_str path, uint32_t mode) {
    char name[4096];
    if (!nova_c_path(path, name)) return nova_io_fail(EINVAL);
    int fd;
    do {
        fd = mode == 0u ? open(name, O_RDONLY) : open(name, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    } while (fd < 0 && errno == EINTR);
    return fd < 0 ? nova_io_errno() : nova_io_ok(fd);
}

nv_io_result nova_file_remove(nv_str path) {
    char name[4096];
    if (!nova_c_path(path, name)) return nova_io_fail(EINVAL);
    return unlink(name) < 0 ? nova_io_errno() : nova_io_ok(0);
}

nv_io_result nova_fd_read(int32_t fd, uint8_t *buffer, uint64_t length) {
    size_t chunk = length > (1u << 30) ? (size_t)1u << 30 : (size_t)length;
    ssize_t n;
    do {
        n = read(fd, buffer, chunk);
    } while (n < 0 && errno == EINTR);
    return n < 0 ? nova_io_errno() : nova_io_ok((int64_t)n);
}

nv_io_result nova_fd_write(int32_t fd, const uint8_t *bytes, uint64_t length) {
    uint64_t done = 0;
    while (done < length) {
        uint64_t rest = length - done;
        size_t chunk = rest > (1u << 30) ? (size_t)1u << 30 : (size_t)rest;
#ifdef MSG_NOSIGNAL
        /* A socket whose peer is gone reports EPIPE instead of raising SIGPIPE (Linux). */
        ssize_t n = send(fd, bytes + done, chunk, MSG_NOSIGNAL);
        if (n < 0 && errno == ENOTSOCK) n = write(fd, bytes + done, chunk);
#else
        ssize_t n = write(fd, bytes + done, chunk);
#endif
        if (n < 0 && errno == EINTR) continue;
        if (n < 0) return nova_io_errno();
        if (n == 0) return nova_io_fail(EIO);
        done += (uint64_t)n;
    }
    return nova_io_ok(0);
}

void nova_fd_drop(int32_t *fd) {
    if (*fd >= 0) (void)close(*fd);
}

/* ---- TCP over IPv4 ---------------------------------------------------------------------- */

/* Dotted IPv4 text ("127.0.0.1") to its four bytes; false if malformed. */
static bool nova_ipv4(nv_str text, uint8_t out[4]) {
    uint64_t i = 0;
    for (int part = 0; part < 4; part++) {
        if (part > 0) {
            if (i >= text.len || text.ptr[i] != '.') return false;
            i++;
        }
        uint32_t value = 0;
        uint64_t digits = 0;
        while (i < text.len && text.ptr[i] >= '0' && text.ptr[i] <= '9' && digits < 3) {
            value = value * 10u + (uint32_t)(text.ptr[i] - '0');
            i++;
            digits++;
        }
        if (digits == 0 || value > 255u) return false;
        out[part] = (uint8_t)value;
    }
    return i == text.len;
}

#ifdef COSMO_AF_INET
/* CosmoOS's own address layout: host-order port, address bytes in network order (sys/socket.h). */
typedef struct sockaddr nova_address;
static void nova_address_set(nova_address *a, const uint8_t bytes[4], uint16_t port) {
    memset(a, 0, sizeof *a);
    a->sa_family = AF_INET;
    a->sa_port = port;
    memcpy(a->sa_addr, bytes, 4);
}
static uint16_t nova_address_port(const nova_address *a) { return a->sa_port; }
#else
typedef struct sockaddr_in nova_address;
static void nova_address_set(nova_address *a, const uint8_t bytes[4], uint16_t port) {
    memset(a, 0, sizeof *a);
#ifdef __APPLE__
    a->sin_len = (uint8_t)sizeof *a;
#endif
    a->sin_family = AF_INET;
    a->sin_port = htons(port);
    memcpy(&a->sin_addr, bytes, 4);
}
static uint16_t nova_address_port(const nova_address *a) { return ntohs(a->sin_port); }
#endif

/* A new TCP socket; on macOS, writes to a closed peer report EPIPE instead of raising SIGPIPE. */
static int nova_tcp_socket(void) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
#ifdef SO_NOSIGPIPE
    if (fd >= 0) {
        int one = 1;
        (void)setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
    }
#endif
    return fd;
}

nv_io_result nova_tcp_listen(nv_str address, uint16_t port) {
    uint8_t bytes[4];
    if (!nova_ipv4(address, bytes)) return nova_io_fail(EINVAL);
    int fd = nova_tcp_socket();
    if (fd < 0) return nova_io_errno();
#ifdef SO_REUSEADDR
    int one = 1;
    (void)setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
#endif
    nova_address a;
    nova_address_set(&a, bytes, port);
    if (bind(fd, (const struct sockaddr *)&a, sizeof a) < 0 || listen(fd, 16) < 0) {
        nv_io_result failed = nova_io_errno();
        (void)close(fd);
        return failed;
    }
    return nova_io_ok(fd);
}

nv_io_result nova_tcp_port(int32_t fd) {
    nova_address a;
    socklen_t length = sizeof a;
    if (getsockname(fd, (struct sockaddr *)&a, &length) < 0) return nova_io_errno();
    return nova_io_ok(nova_address_port(&a));
}

nv_io_result nova_tcp_accept(int32_t fd) {
    int peer;
    do {
        peer = accept(fd, NULL, NULL);
    } while (peer < 0 && errno == EINTR);
    if (peer < 0) return nova_io_errno();
#ifdef SO_NOSIGPIPE
    int one = 1;
    (void)setsockopt(peer, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif
    return nova_io_ok(peer);
}

nv_io_result nova_tcp_connect(nv_str address, uint16_t port) {
    uint8_t bytes[4];
    if (!nova_ipv4(address, bytes)) return nova_io_fail(EINVAL);
    int fd = nova_tcp_socket();
    if (fd < 0) return nova_io_errno();
    nova_address a;
    nova_address_set(&a, bytes, port);
    if (connect(fd, (const struct sockaddr *)&a, sizeof a) < 0) {
        nv_io_result failed = nova_io_errno();
        (void)close(fd);
        return failed;
    }
    return nova_io_ok(fd);
}

nv_io_result nova_tcp_shutdown_write(int32_t fd) {
    return shutdown(fd, SHUT_WR) < 0 ? nova_io_errno() : nova_io_ok(0);
}

/* ---- Processes -------------------------------------------------------------------------- */

/* A NUL-terminated copy from the counted allocator; NULL if the text has a zero byte or memory runs out. */
static char *nova_c_string(nv_str text, int32_t *error) {
    for (uint64_t i = 0; i < text.len; i++) {
        if (text.ptr[i] == 0) { *error = EINVAL; return NULL; }
    }
    char *copy = (char *)nova_alloc((size_t)text.len + 1u, NV_ALLOC_SYSTEM);
    if (copy == NULL) { *error = ENOMEM; return NULL; }
    for (uint64_t i = 0; i < text.len; i++) copy[i] = (char)text.ptr[i];
    copy[text.len] = 0;
    return copy;
}

nv_io_result nova_process_run(nv_str program, const nv_str *arguments, uint64_t count) {
    if (count > 4096u) return nova_io_fail(EINVAL);
    char **argv = (char **)nova_alloc((size_t)(count + 2u) * sizeof(char *), NV_ALLOC_SYSTEM);
    if (argv == NULL) return nova_io_fail(ENOMEM);
    int32_t error = 0;
    uint64_t made = 0;
    argv[0] = nova_c_string(program, &error);
    if (argv[0] != NULL) {
        made = 1;
        for (; made <= count; made++) {
            argv[made] = nova_c_string(arguments[made - 1u], &error);
            if (argv[made] == NULL) break;
        }
    }
    nv_io_result result = nova_io_ok(0);
    if (error != 0) {
        result = nova_io_fail(error);
    } else {
        argv[count + 1u] = NULL;
        (void)fflush(stdout);
        pid_t pid;
#ifdef COSMO_AF_INET
        pid = spawnvp(argv[0], (const char *const *)argv, NULL, 0);
        int started = pid < 0 ? errno : 0;
#else
        int started = posix_spawnp(&pid, argv[0], NULL, NULL, argv, environ);
#endif
        if (started != 0) {
            result = nova_io_fail(started);
        } else {
            int status = 0;
            pid_t waited;
            do {
                waited = waitpid(pid, &status, 0);
            } while (waited < 0 && errno == EINTR);
            if (waited < 0) {
                result = nova_io_errno();
            } else if (WIFEXITED(status)) {
                result = nova_io_ok(WEXITSTATUS(status));
            } else {
                result = nova_io_ok(128 + WTERMSIG(status));
            }
        }
    }
    for (uint64_t i = 0; i < made; i++) nova_free(argv[i], NV_ALLOC_SYSTEM);
    nova_free(argv, NV_ALLOC_SYSTEM);
    return result;
}
