#ifndef UNICODE
#define UNICODE 1
#endif
#include <windows.h>
#include <stdlib.h>

static void write_value(HANDLE file, const void *bytes, DWORD size)
{
    DWORD written = 0;
    if (!WriteFile(file, bytes, size, &written, NULL) || written != size) ExitProcess(91);
}
static void write_string(HANDLE file, const WCHAR *value)
{
    DWORD length = (DWORD)lstrlenW(value);
    write_value(file, &length, sizeof(length));
    write_value(file, value, length * sizeof(WCHAR));
}
int wmain(int argc, wchar_t **argv)
{
    if (argc < 4) return 90;
    HANDLE file = CreateFileW(argv[1], GENERIC_WRITE, 0, NULL, CREATE_NEW, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return 92;
    DWORD count = (DWORD)argc;
    write_value(file, &count, sizeof(count));
    for (int index = 0; index < argc; ++index) write_string(file, argv[index]);
    WCHAR directory[32768];
    GetCurrentDirectoryW(32768, directory);
    write_string(file, directory);
    HANDLE token = NULL;
    TOKEN_ELEVATION elevation = {0};
    DWORD returned = 0;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) ||
        !GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned)) return 93;
    CloseHandle(token);
    write_value(file, &elevation.TokenIsElevated, sizeof(DWORD));
    CloseHandle(file);
    Sleep((DWORD)_wtoi(argv[2]));
    return _wtoi(argv[3]);
}
