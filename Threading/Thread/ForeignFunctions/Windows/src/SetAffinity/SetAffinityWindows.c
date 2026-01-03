#include <windows.h>
#include <stdint.h> // you have to use non-legacy MinGW for your toolchain.

enum OutcomeCode {
	InvalidArgInitialization = INT32_C(-1),
	FailedToGetHandle = INT32_C(-2),
	SetThreadAffinityMaskFailed = INT32_C(-3),
	AppliedMaskDoesNotMatch = INT32_C(-4),
	Success = INT32_C(0)
};

__declspec(dllexport)
int32_t SetAffinityUnsafe(uint64_t suppliedAffinityMask, uint64_t *appliedAffinityMask) {
	if (suppliedAffinityMask <= UINT64_C(0) || appliedAffinityMask == NULL) return InvalidArgInitialization;

	*appliedAffinityMask = UINT64_C(0); // zero out for good practice

	// sets a long to some negative number then casts it as a ptr. 
	// That ptr address doesn't eval to anything, the address is interpreted raw.
	// ensure the value of this address equates to -2.
	HANDLE currThreadHandle = GetCurrentThread(); 

	// DWORD_PTR ISNT ACTUALLY A POINTER. WEIRD MICROSOFT STUFF.
	// Will either return the prior mask as a ulong 64bit, or return 0 which signals an error.
	// Calling the API twice, to compare the output of the applied mask to see if the affinity was properly set.
	uint64_t priorMask = (uint64_t)SetThreadAffinityMask(currThreadHandle, (DWORD_PTR)affinityMask);
	uint64_t appliedMask = (uint64_t)SetThreadAffinityMask(currThreadHandle, (DWORD_PTR)affinityMask);

	if (priorMask == UINT64_C(0) || appliedMask == UINT64_C(0)) {
		// potentially do something with the error code in the future, but for now, this works.
		uint32_t errorCode = (uint32_t)GetLastError();

		return SetThreadAffinityMaskFailed;
	}

	*appliedAffinityMask = appliedMask; // copy the current applied mask into the already allocated ptr to pass back to C#. Even if there's a mismatch.

	if (appliedMask != suppliedAffinityMask) return AppliedMaskDoesNotMatch;

	return Success;
}