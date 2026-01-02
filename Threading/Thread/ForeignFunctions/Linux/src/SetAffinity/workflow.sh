#!/bin/bash

# clear existing files
if [ -e ./SetAffinityLinux.so ]; then
    rm ./SetAffinityLinux.so
    echo "prior .so file deleted"
  else
    echo "No existing .so file"
fi

# compile
gcc -shared -fPIC -o SetAffinityLinux.so SetAffinityLinux.c
echo "Compilation complete"