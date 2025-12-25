#!/bin/bash

# clear existing files
if [ -e ./UnpinThreadLinux.so ]; then
    rm ./UnpinThreadLinux.so
    echo "prior .so file deleted"
  else
    echo "No existing .so file"
fi

# compile
gcc -shared -fPIC -o UnpinThreadLinux.so UnpinThreadLinux.c
echo "Compilation complete"