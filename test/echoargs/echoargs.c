// echoargs.c
// Compile with `cl echoargs.c
#include <windows.h>
#include <stdio.h>

int wmain(int argc, WCHAR** argv) {
    wprintf(L"<%ls>\n", GetCommandLineW());
    for(int i = 1; i < argc; i++) {
        wprintf(L"<%s> ", argv[i]);
    }
    wprintf(L"\n");
}
