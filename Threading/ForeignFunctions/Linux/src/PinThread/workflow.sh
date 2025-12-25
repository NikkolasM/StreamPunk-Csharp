#!/bin/bash

# clear existing files
if [ -e ./PinThreadLinux.so ]; then
    rm ./PinThreadLinux.so
    echo "prior .so file deleted"
  else
    echo "No existing .so file"
fi

# compile
gcc -shared -fPIC -o PinThreadLinux.so PinThreadLinux.c
echo "Compilation complete"