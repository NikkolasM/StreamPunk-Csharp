#!/bin/bash

# clear existing files
if [ -e ./A.so ]; then
    rm ./ResetAffinityLinux.so
    echo "prior .so file deleted"
  else
    echo "No existing .so file"
fi

# compile
gcc -shared -fPIC -o ResetAffinityLinux.so ResetAffinityLinux.c
echo "Compilation complete"