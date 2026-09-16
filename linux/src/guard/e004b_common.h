/* Owned E004b evidence helpers. No game code or runtime installation. */
#ifndef E004B_COMMON_H
#define E004B_COMMON_H
#ifndef _GNU_SOURCE
#define _GNU_SOURCE 1
#endif
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <limits.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

#if !defined(__linux__) || !defined(__x86_64__) || defined(__ILP32__)
#error "This source requires Linux x86_64 LP64."
#endif
#if __BYTE_ORDER__ != __ORDER_LITTLE_ENDIAN__
#error "The accepted filter requires little-endian memory."
#endif

#define E004B_GUARD_FILE "e004b-early-guard.marker"
#define E004B_MARKER_LIMIT 4096U
#define E004B_RUN_ID_LIMIT 128U
#define E004B_BOOTSTRAP_FSIZE UINT64_C(2199023255552)
#define E004B_FILTER_COUNT 93U
#define E004B_FILTER_BYTES 744U

_Static_assert(CHAR_BIT == 8 && sizeof(long) == 8 && sizeof(void *) == 8,
               "LP64 byte and pointer sizes are required");
_Static_assert(sizeof(struct sock_filter) == 8, "sock_filter size mismatch");
_Static_assert(offsetof(struct sock_filter, code) == 0 &&
               offsetof(struct sock_filter, jt) == 2 &&
               offsetof(struct sock_filter, jf) == 3 &&
               offsetof(struct sock_filter, k) == 4, "sock_filter layout mismatch");
_Static_assert(sizeof(struct sock_fprog) == 16 &&
               offsetof(struct sock_fprog, len) == 0 &&
               offsetof(struct sock_fprog, filter) == 8, "sock_fprog layout mismatch");
_Static_assert(sizeof(rlim_t) == 8 && sizeof(off_t) == 8,
               "64-bit rlim_t and off_t are required");
_Static_assert(SYS_prctl == 157 && SYS_seccomp == 317 &&
               SYS_getpid == 39 && SYS_gettid == 186, "syscall ABI mismatch");
_Static_assert(PR_SET_NO_NEW_PRIVS == 38 && PR_GET_NO_NEW_PRIVS == 39 &&
               PR_GET_SECCOMP == 21, "prctl ABI mismatch");
_Static_assert(SECCOMP_SET_MODE_FILTER == 1 && SECCOMP_FILTER_FLAG_TSYNC == 1,
               "seccomp install ABI mismatch");

#include "accepted_filter.h"

/* const-qualified pointer, with exactly the kernel sock_fprog layout. */
struct e004b_program {
    unsigned short len;
    const struct sock_filter *filter;
};
_Static_assert(sizeof(struct e004b_program) == sizeof(struct sock_fprog) &&
               offsetof(struct e004b_program, filter) == offsetof(struct sock_fprog, filter),
               "const program layout mismatch");

struct e004b_result { long raw; int error; };
struct e004b_guard_record {
    const char *run_id;
    long pid, tid;
    struct e004b_result nnp, tsync, nnp_get, seccomp_get;
    uint64_t fsize_soft, fsize_hard;
};

/* All six arguments have a fixed type, also for the separately built test shim. */
static inline struct e004b_result e004b_call(long number,
        unsigned long a, unsigned long b, unsigned long c,
        unsigned long d, unsigned long e, unsigned long f)
{
    errno = 0;
    long raw = syscall(number, a, b, c, d, e, f);
    int error = errno;
    return (struct e004b_result){raw, error};
}

static inline _Noreturn void e004b_fail(int status, const char *stage,
                                       long raw, int error)
{
    char message[256];
    int n = snprintf(message, sizeof(message),
        "e004b_early_guard_failure stage=%s raw=%ld errno=%d exit=%d\n",
        stage, raw, error, status);
    if (n > 0 && (size_t)n < sizeof(message)) {
        ssize_t ignored = write(STDERR_FILENO, message, (size_t)n);
        (void)ignored;
    }
    _exit(status);
}

static inline const char *e004b_run_id(void)
{
    const char *s = getenv("PROCESS_RUN_ID");
    if (s == NULL) e004b_fail(120, "process_run_id_missing", -1, EINVAL);
    for (size_t i = 0; i <= E004B_RUN_ID_LIMIT; ++i) {
        unsigned char c = (unsigned char)s[i];
        if (c == 0) {
            if (i == 0) e004b_fail(120, "process_run_id_empty", -1, EINVAL);
            return s;
        }
        if (i == E004B_RUN_ID_LIMIT ||
            !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
              (c >= '0' && c <= '9') || c == '_' || c == '-'))
            e004b_fail(120, "process_run_id_invalid", -1, EINVAL);
    }
    e004b_fail(120, "process_run_id_unreachable", -1, EINVAL);
}

/* Serialize the bytes at the SAME pointer submitted to seccomp, after TSYNC. */
static inline size_t e004b_format_guard(char out[E004B_MARKER_LIMIT],
        const struct e004b_guard_record *r, const struct sock_filter *installed)
{
    char hex[E004B_FILTER_BYTES * 2U + 1U];
    const volatile unsigned char *memory =
        (const volatile unsigned char *)(const void *)installed;
    const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < E004B_FILTER_BYTES; ++i) {
        unsigned char value = memory[i];
        hex[2U * i] = digits[value >> 4];
        hex[2U * i + 1U] = digits[value & 15U];
    }
    hex[E004B_FILTER_BYTES * 2U] = '\0';
    int n = snprintf(out, E004B_MARKER_LIMIT,
        "schema=e004b-early-guard-v1\n"
        "PROCESS_RUN_ID=%s\npid=%ld\ntid=%ld\n"
        "nnp_raw=%ld\nnnp_errno=%d\ntsync_raw=%ld\ntsync_errno=%d\n"
        "nnp_get_raw=%ld\nnnp_get_errno=%d\n"
        "seccomp_get_raw=%ld\nseccomp_get_errno=%d\n"
        "fsize_soft=%" PRIu64 "\nfsize_hard=%" PRIu64 "\n"
        "filter_instruction_count=93\nfilter_bytes=744\nfilter_hex=%s\n"
        "end=e004b-early-guard-v1\n",
        r->run_id, r->pid, r->tid, r->nnp.raw, r->nnp.error,
        r->tsync.raw, r->tsync.error, r->nnp_get.raw, r->nnp_get.error,
        r->seccomp_get.raw, r->seccomp_get.error,
        r->fsize_soft, r->fsize_hard, hex);
    if (n <= 0 || (size_t)n >= E004B_MARKER_LIMIT)
        e004b_fail(125, "marker_format", n, EOVERFLOW);
    return (size_t)n;
}

/* One fixed /work child; no truncation, unlink, rename, retry or fallback. */
static inline void e004b_write_new(const char *leaf, const char *data, size_t size)
{
    if (size == 0 || size >= E004B_MARKER_LIMIT)
        e004b_fail(125, "marker_size", -1, EOVERFLOW);
    int dirfd = open("/work", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (dirfd < 0) e004b_fail(126, "work_open", -1, errno);
    int fd = openat(dirfd, leaf,
        O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, (mode_t)0600);
    if (fd < 0) e004b_fail(126, "marker_open", -1, errno);
    if (close(dirfd) != 0) e004b_fail(126, "work_close", -1, errno);
    if (fchmod(fd, (mode_t)0600) != 0) e004b_fail(126, "marker_mode", -1, errno);
    struct stat st;
    if (fstat(fd, &st) != 0) e004b_fail(126, "marker_stat_before", -1, errno);
    if (!S_ISREG(st.st_mode) || st.st_nlink != 1 || st.st_uid != geteuid() ||
        (st.st_mode & (mode_t)07777) != (mode_t)0600 || st.st_size != 0)
        e004b_fail(126, "marker_metadata_before", -1, EINVAL);
    errno = 0;
    ssize_t written = write(fd, data, size);
    int write_error = errno;
    if (written != (ssize_t)size)
        e004b_fail(127, "marker_write", (long)written, write_error);
    if (fsync(fd) != 0) e004b_fail(127, "marker_fsync", -1, errno);
    if (fstat(fd, &st) != 0) e004b_fail(127, "marker_stat_after", -1, errno);
    if (!S_ISREG(st.st_mode) || st.st_nlink != 1 || st.st_uid != geteuid() ||
        (st.st_mode & (mode_t)07777) != (mode_t)0600 || st.st_size != (off_t)size)
        e004b_fail(127, "marker_metadata_after", -1, EINVAL);
    if (close(fd) != 0) e004b_fail(127, "marker_close", -1, errno);
}
#endif
