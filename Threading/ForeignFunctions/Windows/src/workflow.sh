#!/bin/bash

# clear existing files
if [ -e ./PinThreadWindows.dll ]; then
    rm ./PinThreadWindows.dll
    echo "prior .dll file deleted"
  else
    echo "No existing .dll file"
fi

# compile
x86_64-w64-mingw32-gcc -shared -o PinThreadWindows.dll PinThreadWindows.c -lkernel32
echo "Compilation complete"