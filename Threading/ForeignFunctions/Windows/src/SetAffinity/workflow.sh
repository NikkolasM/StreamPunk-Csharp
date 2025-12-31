#!/bin/bash

# clear existing files
if [ -e ./SetAffinityWindows.dll ]; then
    rm ./SetAffinityWindows.dll
    echo "prior .dll file deleted"
  else
    echo "No existing .dll file"
fi

# compile
x86_64-w64-mingw32-gcc -shared -o SetAffinityWindows.dll SetAffinityWindows.c -lkernel32
echo "Compilation complete"