#include <>
using "std";

wchar* encodeArgument(const wchar* arg, bool wrap) {
    int length = 0;
    int encounteredSlashes = 0;
    int i = 0;
    outer:
    for(int i = 0; ; i++) {
        switch(arg[i]) {
            case 0:
                length += encounteredSlashes * 2;
                break outer;
            case '\\':
                encounteredSlashes++;
                break;
            case '"':
                length += encounteredSlashes * 2 + 2;
                break;
            default:
                length++;
        }
    }
    if(wrap) length++;
    wchar* ret = malloc(sizeof(wchar) * length + 1);
    i = 0;
    int j = 0;
    while(true) {
        switch(arg[i]) {
            case 0:
                length += encounteredSlashes * 2;
                break outer;
            case '\\':
                encounteredSlashes++;
                break;
            case '"':
                length += encounteredSlashes * 2 + 2;
                break;
            default:
                length++;
        }
    }
}