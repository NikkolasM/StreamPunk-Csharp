#define _GNU_SOURCE
#include <unistd.h> 
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h> 

enum OutcomeCode {
    InvalidArgInitialization = INT32_C(-1),
    FailedToGetRealNumCpus = INT32_C(-2),
    TooManyCpus = INT32_C(-3),
    FailedToAllocCpuSet = INT32_C(-4),
    FailedToSetAffinity = INT32_C(-5),
    FailedToAllocComparisonCpuSet = INT32_C(-6),
    FailedToGetAffinity = INT32_C(-7),
    FailedToAllocComparisonMask = INT32_C(-8),
    Success = INT32_C(0)
};

int32_t __cdecl ResetAffinityUnsafe(
	int32_t* tid,
	uint64_t** appliedAffinityMask, // outer pointer is the referece in C#, inner pointer is the actual array which is fundamentally a pointer to index 0 of the array on that given data type.
	uint64_t* appliedMaskLength
) {
    if (tid == NULL || appliedAffinityMask == NULL || appliedMaskLength == NULL) return InvalidArgInitialization;

    // Zero out the args that represent buffs the caller can access. 
    // Compatible for .NET 10, but zeroing out outbound buffs is good practice regardless.
    *tid = INT32_C(0);
    *appliedAffinityMask = NULL;
    *appliedMaskLength = UINT64_C(0);

    //***GET THE NUM OF CPUS FROM THE KERNEL AND THEN SET THOSE CPUs ALL TO 1 IN THE BITMASK TO ENABLE THEM

    int64_t numOfCpus = (int64_t)sysconf(_SC_NPROCESSORS_CONF);
	if (numOfCpus <= INT64_C(0)) return FailedToGetRealNumCpus;
    if (numOfCpus > (int64_t)INT32_MAX) return TooManyCpus; // needs to fit since it will be passed to CPU_ALLOC, which expects a 32bit signed int.

	cpu_set_t* cpuset = CPU_ALLOC((int32_t)numOfCpus);
	if (cpuset == NULL) return FailedToAllocCpuSet;

	size_t size = CPU_ALLOC_SIZE((int32_t)numOfCpus);
	CPU_ZERO_S(size, cpuset);

	// safe to cast to unsigned, because the prior <= 0 check ensures the value 'numOfCpus' isn't negative, nor over the int32 max
	// Most-significant-bit is what's used to define whether a signed integer is positive or negative.
    // 0-indexing for the CPU numbers
	for (int32_t i = INT32_C(0); i < (int32_t)numOfCpus; i++) CPU_SET_S(i, size, cpuset);

	int32_t outcomeCode = (int32_t)sched_setaffinity(INT32_C(0), size, cpuset); // 0 for pid means the current executing thread. 
	CPU_FREE(cpuset);

	if (outcomeCode < INT32_C(0)) return FailedToSetAffinity;

    // ***NOW GET THE AFFINITY FROM THE KERNEL TO ENSURE THE UPDATED AFFINITY MATCHES 

    int64_t realNumOfCpus = (int64_t)sysconf(_SC_NPROCESSORS_CONF);
    if (realNumOfCpus <= INT64_C(0)) return FailedToGetRealNumCpus;
    if (realNumOfCpus > (int64_t)INT32_MAX) return TooManyCpus; // needs to fit since it will be passed to CPU_ALLOC, which expects a 32bit signed int.

    cpu_set_t* realcpuset = CPU_ALLOC((int32_t)realNumOfCpus);
    if (realcpuset == NULL) return FailedToAllocComparisonCpuSet;

    size_t realSize = CPU_ALLOC_SIZE((int32_t)realNumOfCpus); // expects an unsigned long 64bit, so type casting a positive signed long to unsigned long 64bit is safe
    CPU_ZERO_S(realSize, realcpuset);

    int32_t getAffinityOutcomeCode = (int32_t)(sched_getaffinity(INT32_C(0), realSize, realcpuset));
    if (getAffinityOutcomeCode < INT32_C(0)) {
        CPU_FREE(realcpuset);
        return FailedToGetAffinity;
    }

    // adds 64 bits to the num of CPUs to move forward one bucket,then divides by 64 which applies what's essentially a division + Math.floor().
    int64_t numOfLongs = (realNumOfCpus + INT64_C(64)) / INT64_C(64);

    // Multiply by 64 to get the number of total bits. Divide by 8 to get the number of total bytes
    // safe to cast numOfLongs to uint64, since numOfLongs should always be positive.
    uint64_t numOfBytes = (((uint64_t)numOfLongs) * UINT64_C(64)) / UINT64_C(8);

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
            uint64_t currIndexOfComparisonMask = ((uint64_t)numOfLongs) - ((i + UINT64_C(8)) / UINT64_C(8));

            comparisonMask[currIndexOfComparisonMask] = ((comparisonMask[currIndexOfComparisonMask] << UINT64_C(1)) | ((uint64_t)(extractedBit)));
        }
    }

    CPU_FREE(realcpuset);

    *tid = (int32_t)(gettid());
    *appliedAffinityMask = comparisonMask;
    *appliedMaskLength = numOfLongs;

	return Success;
}