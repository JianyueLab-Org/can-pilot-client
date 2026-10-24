// CAN Pilot.app 的通用（arm64 + x86_64）入口。
//
// .NET 的单文件程序没法用 lipo 合成一个通用二进制（单文件包按文件内偏移定位），
// 所以 .app 里放两份：can-pilot-arm64、can-pilot-x64。这个小程序本身编成通用二进制，
// 系统按本机芯片挑对应的切片运行，它再 exec 同架构的那一份。进程号不变，
// 麦克风、输入监控这些权限仍记在 CAN Pilot.app 名下。
//
//   clang -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -o can-pilot tools/macos-launcher.c
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    (void)argc;
    char self[PATH_MAX];
    uint32_t size = sizeof self;
    if (_NSGetExecutablePath(self, &size) != 0)
        return 127;
    char resolved[PATH_MAX];
    if (realpath(self, resolved) == NULL)
        return 127;
    char *slash = strrchr(resolved, '/');
    if (slash == NULL)
        return 127;
    *slash = '\0';

#if defined(__arm64__) || defined(__aarch64__)
    const char *name = "can-pilot-arm64";
#else
    const char *name = "can-pilot-x64";
#endif
    char target[PATH_MAX];
    if (snprintf(target, sizeof target, "%s/%s", resolved, name) >= (int)sizeof target)
        return 127;
    argv[0] = target;
    execv(target, argv);
    perror(target);
    return 127;
}
