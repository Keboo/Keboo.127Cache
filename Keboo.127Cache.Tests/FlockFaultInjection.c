#define _POSIX_C_SOURCE 200809L

#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

typedef int (*flock_function)(int, int);

#if defined(K127_DIRECT_SHIM)

int flock(int fd, int operation)
{
    flock_function injected = (flock_function)dlsym(RTLD_DEFAULT, "k127_fault_flock");
    if (injected == NULL)
    {
        errno = ENOSYS;
        return -1;
    }
    return injected(fd, operation);
}

#else

static atomic_int injected;

static void signal_fault(const char *signal_path)
{
    size_t length = strlen(signal_path) + sizeof(".pending");
    char *pending_path = malloc(length);
    if (pending_path == NULL)
        _exit(86);
    snprintf(pending_path, length, "%s.pending", signal_path);

    char code[32];
    int count = snprintf(code, sizeof(code), "%d", ENOLCK);
    int output = open(pending_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (output < 0 || write(output, code, (size_t)count) != count ||
        close(output) != 0 || rename(pending_path, signal_path) != 0)
        _exit(86);
    free(pending_path);
}

int k127_fault_flock(int fd, int operation)
{
    flock_function actual = (flock_function)dlsym(RTLD_NEXT, "flock");
    if (actual == NULL)
    {
        errno = ENOSYS;
        return -1;
    }

    const char *target = getenv("K127_FAULT_LOCK_PATH");
    const char *signal_path = getenv("K127_FAULT_SIGNAL");
    const char *continue_path = getenv("K127_FAULT_CONTINUE");
    struct stat expected, opened;
    if (target != NULL && signal_path != NULL && continue_path != NULL &&
        (operation & (LOCK_EX | LOCK_NB)) == (LOCK_EX | LOCK_NB) &&
        stat(target, &expected) == 0 && fstat(fd, &opened) == 0 &&
        expected.st_dev == opened.st_dev && expected.st_ino == opened.st_ino &&
        atomic_exchange(&injected, 1) == 0)
    {
        /* An attempted upgrade can lose an existing shared lock before failing. */
        if (actual(fd, LOCK_UN) != 0)
            _exit(86);
        signal_fault(signal_path);

        struct timespec delay = { 0, 10000000 };
        for (int attempt = 0; attempt < 6000; attempt++)
        {
            if (access(continue_path, F_OK) == 0)
            {
                errno = ENOLCK;
                return -1;
            }
            nanosleep(&delay, NULL);
        }
        _exit(86);
    }

    return actual(fd, operation);
}

#if defined(__APPLE__)
__attribute__((used)) static const struct
{
    const void *replacement;
    const void *replacee;
} interpose[] __attribute__((section("__DATA,__interpose"))) = {
    { (const void *)k127_fault_flock, (const void *)flock }
};
#else
int flock(int fd, int operation)
{
    return k127_fault_flock(fd, operation);
}
#endif

#endif
