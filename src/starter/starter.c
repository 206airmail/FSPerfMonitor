// starter.c - SVSPerfMonitor.exe, the file players double-click. A GUI-subsystem program, so Windows never gives it a
// console window (Windows 10 included). It starts the real program, SVSPerfMonitor.com next to it (a console program,
// so a terminal and scripts get its output), and leaves:
//   - no arguments or --tray: the tray app, started with CREATE_NO_WINDOW (no console window on any Windows) + --tray-child
//   - anything else (command line): runs the .com in the console we were started from, waits, returns its exit code
// No C runtime (tiny, no dependencies): kernel32 only. Copyright (c) 2026 Jared Brown. MIT licence.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define MAXCMD 32768

// the arguments after argv[0] in our command line (CommandLineToArgvW rules for the program name)
static const wchar_t *SkipProgramName(const wchar_t *p)
{
    if (*p == L'"') { p++; while (*p && *p != L'"') p++; if (*p) p++; }
    else { while (*p && *p != L' ' && *p != L'\t') p++; }
    while (*p == L' ' || *p == L'\t') p++;
    return p;
}

// true when the argument string has no arguments, or has --tray as a whole word
static BOOL IsTray(const wchar_t *a)
{
    if (!*a) return TRUE;
    for (const wchar_t *p = a; *p; p++) {
        if ((p == a || p[-1] == L' ' || p[-1] == L'\t') && p[0] == L'-' && p[1] == L'-' && p[2] == L't' && p[3] == L'r' &&
            p[4] == L'a' && p[5] == L'y' && (p[6] == 0 || p[6] == L' ' || p[6] == L'\t' || p[6] == L'"')) return TRUE;
    }
    return FALSE;
}

// no C runtime, so no memset: a byte loop the compiler won't turn back into a memset call
static void Zero(void *p, SIZE_T n)
{
    volatile unsigned char *b = (volatile unsigned char *)p;
    while (n--) *b++ = 0;
}

static void Fail(const wchar_t *what)
{
    MessageBoxW(NULL, what, L"SVS Performance Monitor", MB_ICONWARNING);
}

void Start(void)
{
    static wchar_t exe[MAX_PATH + 8], cmd[MAXCMD];
    DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) ExitProcess(5);
    // ...\SVSPerfMonitor.exe -> ...\SVSPerfMonitor.com
    wchar_t *dot = exe + n; while (dot > exe && *dot != L'.' && *dot != L'\\') dot--;
    if (*dot != L'.') ExitProcess(5);
    lstrcpyW(dot, L".com");
    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES) {
        Fail(L"SVSPerfMonitor.com is missing. It must be in the same folder as SVSPerfMonitor.exe: unpack the whole download again.");
        ExitProcess(5);
    }
    const wchar_t *args = SkipProgramName(GetCommandLineW());
    BOOL tray = IsTray(args);
    if (lstrlenW(exe) + lstrlenW(args) + 32 >= MAXCMD) ExitProcess(5);
    cmd[0] = L'"'; lstrcpyW(cmd + 1, exe); lstrcatW(cmd, L"\"");
    if (*args) { lstrcatW(cmd, L" "); lstrcatW(cmd, args); }
    if (tray) lstrcatW(cmd, L" --tray-child");

    STARTUPINFOW si; PROCESS_INFORMATION pi;
    Zero(&si, sizeof si); Zero(&pi, sizeof pi); si.cb = sizeof si;
    if (tray) {
        // the tray app: no console window, no inherited handles
        if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
            Fail(L"SVS Performance Monitor could not be started.");
            ExitProcess(5);
        }
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        ExitProcess(0);
    }
    // the command line: share the caller's console (or its redirected output) and pass the exit code through
    AttachConsole(ATTACH_PARENT_PROCESS);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    if (!CreateProcessW(exe, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) ExitProcess(5);
    CloseHandle(pi.hThread);
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1; GetExitCodeProcess(pi.hProcess, &code);
    ExitProcess(code);
}
