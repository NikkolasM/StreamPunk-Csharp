#!/bin/bash

# clear existing files
if [ -e ./UnpinThreadWindows.dll ]; then
    rm ./UnpinThreadWindows.dll
    echo "prior .dll file deleted"
  else
    echo "No existing .dll file"
fi

# compile
x86_64-w64-mingw32-gcc -shared -o UnpinThreadWindows.dll UnpinThreadWindows.c -lkernel32
echo "Compilation complete"