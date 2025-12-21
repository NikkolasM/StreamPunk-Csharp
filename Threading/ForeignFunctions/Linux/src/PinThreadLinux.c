#define _GNU_SOURCE
#include <unistd.h> // for gettid()
#include <sched.h> // for sched_setaffinity(), pid_t, cpu_set_t*, size_t
#include <stdint.h>
#include <stdlib.h>

// Accepts a callback so the FF can return data, such as the thread ID of the given context.
// Useful if you need to get data to understand your topology in relation to the OS.

int PinThreadLinux(unsigned long mask[], unsigned int maskLength, int *tid) {
    *tid = (int)gettid(); // memory for pointer already allocated, just need to save the thread ID to it. should clone into that same-sized heap 

    if (maskLength == 0) return -1;

    unsigned int lastIndex = maskLength - 1;
    unsigned int numOfCpus = maskLength * 64; // because 64 bits per long

    cpu_set_t* cpuset = CPU_ALLOC(numOfCpus);

    if (cpuset == NULL) return -1;

    size_t size = CPU_ALLOC_SIZE(numOfCpus);
    CPU_ZERO_S(size, cpuset);

    // iterate starting from the end from right to left. 
    // follows typical binary incrementation, furthest bit right is core 1, etc.
    // 'i' is the array index and 'j' is the bit position
    for (int i = lastIndex; i >= 0; i--) {
        for (int j = 0; j < 64; j++) {
            unsigned long extractionMask = 1UL << j; // 0-based index
            unsigned long currLong = mask[i]; 
            unsigned long extractedBit = (extractionMask & currLong) >> j;

                if (extractedBit == 1) {
                    unsigned int realBitPosition = j + ((lastIndex - i) * 64); // 64 bits per long
                    CPU_SET_S(realBitPosition, size, cpuset);
                }
        }
    }

    int outcomeCode = sched_setaffinity(0, size, cpuset);

    CPU_FREE(cpuset);

    return outcomeCode;
}