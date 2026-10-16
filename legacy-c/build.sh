#!/bin/sh
set -e
cd "$(dirname "$0")"
python3 make_icons.py
LIBS="-lole32 -lshell32 -luser32 -lgdi32 -lcomctl32 -lwinmm"
x86_64-w64-mingw32-windres app.rc -o app_res.o
echo "--- GUI build"
x86_64-w64-mingw32-gcc -O2 -Wall -Wno-unused-but-set-variable \
    -mwindows ptt_mic.c app_res.o -o PushToTalk.exe \
    -lole32 -lshell32 -luser32 -lgdi32 -lcomctl32 -static -s
echo "--- selftest build"
x86_64-w64-mingw32-gcc -O2 -Wall -DAPP_SELFTEST \
    -mconsole ptt_mic.c app_res.o -o ptt_selftest.exe \
    -lole32 -lshell32 -luser32 -lgdi32 -lcomctl32 -static -s
ls -la PushToTalk.exe ptt_selftest.exe
