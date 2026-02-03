#!/bin/bash

# clear existing files
if [ -e ./GetAffinityLinux.so ]; then
    rm ./GetAffinityLinux.so
    echo "prior .so file deleted"
  else
    echo "No existing .so file"
fi

# compile
gcc -shared -fPIC -o GetAffinityLinux.so GetAffinityLinux.c
echo "Compilation complete"