#define _GNU_SOURCE
#include <unistd.h> 
#include <sched.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <limits.h>  
#include <stdbool.h> 

// suppliedAffinityMask you read right to left on both the array and the 64bit longs.
// mask length is the length of the arbitrarily sized array where the size of cells are unsigned long.
// **appliedAffinityMask and *appliedMaskLength are important so that the calling C# can test on what is returned.

enum OutcomeCode {
    InvalidArgInitialization = INT32_C(-1),
    FailedToAllocCpuSet = INT32_C(-2),
    RealBitPositionTooLarge = INT32_C(-3),
    FailedToSetAffinity = INT32_C(-4),
    FailedToGetRealNumCpus = INT32_C(-5),
    FailedToAllocRealCpuSet = INT32_C(-6),
    FailedToGetAffinity = INT32_C(-7),
    FailedToAllocComparisonMask = INT32_C(-8),
    Success = INT32_C(0)
};

int32_t SetAffinityUnsafe(
    uint64_t *suppliedAffinityMask,
    uint64_t maskLength,
    int32_t *tid,
    uint64_t **appliedAffinityMask, // outer pointer is the referece in C#, inner pointer is the actual array which is fundamentally a pointer to index 0 of the array on that given data type.
    uint64_t *appliedMaskLength
) {
    // check to ensure that there are real pointers to set values to.
    // also check to ensure that maskLength isn't 0.
    if (suppliedAffinityMask == NULL || maskLength == UINT64_C(0) || tid == NULL || appliedAffinityMask == NULL || appliedMaskLength == NULL) return InvalidArgInitialization;

    // Zero out the args that represent buffs the caller can access. 
    // Compatible for .NET 10, but zeroing out outbound buffs is good practice regardless.
    *tid = INT32_C(0);
    *appliedAffinityMask = NULL;
    *appliedMaskLength = UINT64_C(0);

    *tid = (int32_t)gettid(); // memory for pointer already allocated, just need to save the thread ID to the value at the address.

    uint64_t suppliedNumOfCpus = maskLength * UINT64_C(64); // because 64 bits per long

    cpu_set_t* cpuset = CPU_ALLOC(suppliedNumOfCpus);

    size_t size = CPU_ALLOC_SIZE(suppliedNumOfCpus);
    CPU_ZERO_S(size, cpuset);

    // iterate on the array from right to left
    // Little endian per 64bit long element.
    // 'i' is the array index and 'j' is the bit position from right to left per element.

    // Bits use 0 index for the CPU number expected by CPU_SET_S, but the iterator is from maskLength -> 1 so that 
    // it doesn't require using a signed integer type for -1 when at the end of the iteration. 
    // This allows the use of unsigned long long everywhere necessary.
    for (uint64_t i = maskLength; i > UINT64_C(0); i--) {
        uint64_t currLong = suppliedAffinityMask[i - UINT64_C(1)]; // subtract by 1 to convert to 0-based index on the array.

        for (uint64_t j = UINT64_C(0); j < UINT64_C(64); j++) {
            uint64_t extractionMask = UINT64_C(1) << j; // 0-based index. extraction per array cell 

            // extracts the given bit all the way back to the right, so an unsigned char works.
            // This unsigned char will equal either 0 or 1.
            uint8_t extractedBit = (uint8_t)((extractionMask & currLong) >> j);

                if (extractedBit) {
                    uint64_t realBitPosition = j + ((maskLength - i) * UINT64_C(64)); // in relation to the entire arbitrarily long bitmask, not just per 64bit long.

                    // ensures whatever was computed as the real bit position can actually be supplied 
                    // safely to CPU_SET_S(), since the CPU argument takes a signed 32bit int.
                    if (realBitPosition > INT_MAX) return RealBitPositionTooLarge;

                    CPU_SET_S((int32_t)realBitPosition, size, cpuset);
                }
        }
    }

    int32_t setAffinityOutcomeCode = (int32_t)sched_setaffinity(INT32_C(0), size, cpuset); // pid=0 means the current executing thread.

    CPU_FREE(cpuset);

    if (setAffinityOutcomeCode < INT32_C(0)) return FailedToSetAffinity;

    // ***NOW GET THE AFFINITY FROM THE KERNEL TO ENSURE THE UPDATED AFFINITY MATCHES WHAT WAS SUPPLIED***

    // essentially go through a similar process as before, but now you define the mask based on the number of 
    // real CPUs available, getting the affinity, and then converting the cpu_set_t bitmask back to the specific
    // little endian style of this function's interface and passing it back out to the calling C#.

    int64_t realNumOfCpus = (int64_t)sysconf(_SC_NPROCESSORS_CONF); // Not standard POSIX, but it works
    if (realNumOfCpus < INT64_C(0)) return FailedToGetRealNumCpus;

    cpu_set_t* realcpuset = CPU_ALLOC(realNumOfCpus);
    if (realcpuset == NULL) return FailedToAllocRealCpuSet;

    size_t realSize = CPU_ALLOC_SIZE(UINT64_C(realNumOfCpus)); // expects an unsigned long 64bit, so type casting a positive signed long to unsigned long 64bit is safe
    CPU_ZERO_S(realSize, realcpuset);

    int32_t getAffinityOutcomeCode = sched_getaffinity(INT32_C(0), realSize, realcpuset);

    if (getAffinityOutcomeCode < INT32_C(0)) {
        CPU_FREE(realcpuset);
        return FailedToGetAffinity;
    }

    // adds 64 bits to the num of CPUs to move forward one bucket, 
    // then divides by 64 which applies what's essentially a Math.floor() type division. Returns how many unsigned longs there are
    // Multiply by 64 to get the number of total bits
    // Divide by 8 to get the number of total bytes
    uint64_t numOfLongs = ((realNumOfCpus + UINT64_C(64)) / UINT64_C(64));
    uint64_t numOfBytes = (numOfLongs * UINT64_C(64)) / UINT64_C(8);
    uint64_t* comparisonMask = (uint64_t*)calloc(numOfLongs, sizeof(uint64_t));

    if (comparisonMask == NULL) {
        CPU_FREE(realcpuset);
        return FailedToAllocComparisonMask;
    }

    // The num of indexes on realNumOfCpus matches that of the total bytes - 1 
    // the bitmask in cpu_set_t iterates from left to right where each index is a byte.
    // However, per byte, the CPU numbering is from right to left along the bits i.e. byte at i=0, the furthest right bit in that byte is CPU 0
    for (uint64_t i = UINT64_C(0); i < numOfBytes; i++) {
        uint8_t currByte = ((uint8_t*)realcpuset)[i];

        // 7 to 0 because 8 bits per byte using 0 indexing.
        for (uint8_t j = UINT8_C(0); j < UINT8_C(8); j++) {
            uint8_t extractionMask = UINT8_C(1) << j; // current byte extraction mask
            uint8_t extractedBit = (extractionMask & currByte) >> j; // extracted bit in the currently selected byte

            // Derive the equivalent index in the comparison mask this iteration is in
            // iterate right to left. (i + 1 byte) / 8 where dividing by 8 is to bucket bytes to 64 bit longs.
            uint64_t currIndexOfComparisonMask = numOfLongs - ((i + UINT64_C(8)) / UINT64_C(8)); 

            comparisonMask[currIndexOfComparisonMask] = ((comparisonMask[currIndexOfComparisonMask] << UINT64_C(1)) | ((uint64_t)extractedBit));
        }
    }

    CPU_FREE(realcpuset);

    // save the mem address of comparisonMask to the first level derefed value of 
    // appliedAffinityMask. Because comparisonMask evaluates to an arr, which is represented
    // as a mem address to the first element, this works. Just have to make sure that the # of bits on the receiving
    // data type in the C# matches, so that it can perform proper pointer arithmetic for array indexing.
    *appliedAffinityMask = comparisonMask; 
    *appliedMaskLength = numOfLongs;

    return Success;
}
