#ifndef UNICODE
#define UNICODE 1
#endif
#ifndef _UNICODE
#define _UNICODE 1
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* No shell, runtime extraction, registry writes or user data writes. The child owns
   the update lease; keeping this process alive also makes the entry point visible
   to setup's exact-directory process check. */
static DWORD fail(DWORD error, const WCHAR *message)
{
    if (!error) error = ERROR_GEN_FAILURE;
    STARTUPINFOW startup = {0};
    startup.cb = sizeof(startup);
    GetStartupInfoW(&startup);
    // Explicitly hidden automation receives the error code without a modal dialog.
    if (!(startup.dwFlags & STARTF_USESHOWWINDOW) || startup.wShowWindow != SW_HIDE)
        MessageBoxW(NULL, message, L"顺手工具箱", MB_OK | MB_ICONERROR);
    return error;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show)
{
    (void)instance; (void)previous; (void)command;
    // Surface loader failures through the single controlled error path below.
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    WCHAR root[32768], child[32768];
    DWORD length = GetModuleFileNameW(NULL, root, 32768);
    if (!length || length >= 32768)
        return (int)fail(GetLastError(), L"无法确定软件位置。请重新解压完整的软件包。");
    while (length && root[length - 1] != L'\\') --length;
    if (!length) return (int)fail(ERROR_PATH_NOT_FOUND, L"无法确定软件文件夹。");
    // Keep the separator so a portable package placed at C:\ resolves C:\,
    // rather than the unrelated per-drive current directory represented by C:.
    root[length] = 0;
    const WCHAR suffix[] = L"app\\Shunshou.App.exe";
    if (length + (sizeof(suffix) / sizeof(WCHAR)) >= 32768)
        return (int)fail(ERROR_FILENAME_EXCED_RANGE, L"软件路径过长，请将软件放在较短的路径中。");
    lstrcpyW(child, root);
    lstrcatW(child, suffix);
    DWORD attributes = GetFileAttributesW(child);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
        return (int)fail(ERROR_FILE_NOT_FOUND, L"缺少 app 文件夹中的程序。请将整个软件包解压到同一文件夹后再打开。");

    /* argv[0] uses Windows' first-token quoting rule. Preserve every remaining
       UTF-16 code unit so empty arguments, quotes and trailing slashes survive. */
    const WCHAR *tail = GetCommandLineW();
    BOOL quoted = FALSE;
    while (*tail) {
        if (*tail == L'"') quoted = !quoted;
        else if (!quoted && (*tail == L' ' || *tail == L'\t')) break;
        ++tail;
    }
    SIZE_T needed = (SIZE_T)lstrlenW(child) + lstrlenW(tail) + 3;
    if (needed > 32767) return (int)fail(ERROR_FILENAME_EXCED_RANGE, L"启动参数过长。");
    WCHAR *line = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, needed * sizeof(WCHAR));
    if (!line) return (int)fail(ERROR_NOT_ENOUGH_MEMORY, L"内存不足，无法启动软件。");
    line[0] = L'"';
    lstrcpyW(line + 1, child);
    lstrcatW(line, L"\"");
    lstrcatW(line, tail);
    STARTUPINFOW start = {0};
    PROCESS_INFORMATION process = {0};
    start.cb = sizeof(start);
    start.dwFlags = STARTF_USESHOWWINDOW;
    start.wShowWindow = (WORD)show;
    BOOL launched = CreateProcessW(child, line, NULL, NULL, FALSE, 0, NULL, root, &start, &process);
    DWORD error = launched ? ERROR_SUCCESS : GetLastError();
    HeapFree(GetProcessHeap(), 0, line);
    if (!launched) return (int)fail(error, L"软件未能启动。请重新解压完整软件包，并确认 app 文件夹中的文件未被移动。");
    CloseHandle(process.hThread);
    DWORD result = 1;
    if (WaitForSingleObject(process.hProcess, INFINITE) == WAIT_OBJECT_0)
        GetExitCodeProcess(process.hProcess, &result);
    else result = GetLastError();
    CloseHandle(process.hProcess);
    return (int)result;
}
