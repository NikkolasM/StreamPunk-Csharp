#!/bin/bash

# clear existing files
if [ -e ./ResetAffinityWindows.dll ]; then
    rm ./ResetAffinityWindows.dll
    echo "prior .dll file deleted"
  else
    echo "No existing .dll file"
fi

# compile
x86_64-w64-mingw32-gcc -shared -o ResetAffinityWindows.dll ResetAffinityWindows.c -lkernel32
echo "Compilation complete"