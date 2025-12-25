#define _GNU_SOURCE
#include <unistd.h> 
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h> 

enum OutcomeCode {
	FailedToGetRealNumCpus = INT32_C(-1),
	FailedToAllocCpuSet = INT32_C(-2),
	FailedToSetAffinity = INT32_C(-3),
	Success = INT32_C(0)
};

int32_t UnpinThread() {
	int64_t numOfCpus = sysconf(_SC_NPROCESSORS_CONF);

	if (numOfCpus <= INT64_C(0)) return FailedToGetRealNumCpus;

	cpu_set_t* cpuset = CPU_ALLOC(numOfCpus);
	if (cpuset == NULL) return FailedToAllocCpuSet;

	size_t size = CPU_ALLOC_SIZE(numOfCpus);
	CPU_ZERO_S(size, cpuset);

	// safe to cast to unsigned, because the prior check ensures the
	// value 'numOfCpus' isn't negative.
	for (uint64_t i = UINT64_C(0); i < (uint64_t)numOfCpus; i++) {
		CPU_SET_S(i, size, cpuset);
	}

	int32_t outcomeCode = (int32_t)sched_setaffinity(INT32_C(0), size, cpuset); // 0 for pid means the current executing thread. 
	CPU_FREE(cpuset);

	if (outcomeCode < INT32_C(0)) return FailedToSetAffinity;

	return Success;
}