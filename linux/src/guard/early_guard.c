/* Production helper: fixed accepted BPF; no test hooks and no exported API. */
#include "e004b_common.h"

__attribute__((constructor(101)))
static void e004b_early_guard_constructor(void)
{
    struct e004b_guard_record r = {0};
    r.run_id = e004b_run_id();
    struct e004b_result pid = e004b_call(SYS_getpid, 0, 0, 0, 0, 0, 0);
    struct e004b_result tid = e004b_call(SYS_gettid, 0, 0, 0, 0, 0, 0);
    if (pid.raw <= 0 || tid.raw <= 0)
        e004b_fail(120, "process_identity", -1, EINVAL);
    r.pid = pid.raw;
    r.tid = tid.raw;
    struct e004b_program program = {
        .len = (unsigned short)E004B_FILTER_COUNT, .filter = e004b_filter
    };

    r.nnp = e004b_call(SYS_prctl, PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0, 0);
    if (r.nnp.raw != 0)
        e004b_fail(121, "nnp_install", r.nnp.raw, r.nnp.error);
    r.tsync = e004b_call(SYS_seccomp, SECCOMP_SET_MODE_FILTER,
        SECCOMP_FILTER_FLAG_TSYNC, (unsigned long)(uintptr_t)&program, 0, 0, 0);
    if (r.tsync.raw != 0)
        e004b_fail(122, "tsync_install", r.tsync.raw, r.tsync.error);

    r.nnp_get = e004b_call(SYS_prctl, PR_GET_NO_NEW_PRIVS, 0, 0, 0, 0, 0);
    r.seccomp_get = e004b_call(SYS_prctl, PR_GET_SECCOMP, 0, 0, 0, 0, 0);
    if (r.nnp_get.raw != 1 || r.nnp_get.error != 0)
        e004b_fail(123, "nnp_get", r.nnp_get.raw, r.nnp_get.error);
    if (r.seccomp_get.raw != 2 || r.seccomp_get.error != 0)
        e004b_fail(123, "seccomp_get", r.seccomp_get.raw, r.seccomp_get.error);
    struct rlimit fsize;
    if (getrlimit(RLIMIT_FSIZE, &fsize) != 0)
        e004b_fail(124, "bootstrap_fsize_read", -1, errno);
    r.fsize_soft = (uint64_t)fsize.rlim_cur;
    r.fsize_hard = (uint64_t)fsize.rlim_max;
    if (r.fsize_soft != E004B_BOOTSTRAP_FSIZE || r.fsize_hard != E004B_BOOTSTRAP_FSIZE)
        e004b_fail(124, "bootstrap_fsize_value", -1, EINVAL);

    char marker[E004B_MARKER_LIMIT];
    size_t size = e004b_format_guard(marker, &r, program.filter);
    e004b_write_new(E004B_GUARD_FILE, marker, size);
    /* Sole successful return, after NNP, TSYNC, kernel queries and marker I/O. */
}
