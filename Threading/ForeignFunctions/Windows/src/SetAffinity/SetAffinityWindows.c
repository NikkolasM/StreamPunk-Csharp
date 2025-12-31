#include <windows.h>
#include <stdint.h> // you have to use non-legacy MinGW for your toolchain.

enum OutcomeCode {
	FailedToGetHandle = INT32_C(-1),
	SetThreadAffinityMaskFailed = INT32_C(-2),
	Success = INT32_C(0)
};

__declspec(dllexport)
int32_t SetAffinityUnsafe(uint64_t suppliedAffinityMask, uint64_t *appliedAffinitymask) {

	if (suppliedAffinityMask <= UINT64_C(0) || appliedAffinitymask == NULL)

	// sets a long to some negative number then casts it as a ptr. 
	// That ptr address doesn't eval to anything, the address is interpreted raw.
	// ensure the value of this address equates to -2.
	HANDLE currThreadHandle = GetCurrentThread(); 

	if (((int64_t)currThreadHandle) != INT64_C(-2)) return FailedToGetHandle;

	// DWORD_PTR ISNT ACTUALLY A POINTER. WEIRD MICROSOFT STUFF.
	// Will either return the prior mask as a ulong 64bit, or return 0 which signals an error.
	uint64_t priorMask = (uint64_t)SetThreadAffinityMask(currThreadHandle, (DWORD_PTR)affinityMask);
	uint64_t appliedMask = (uint64_t)SetThreadAffinityMask(currThreadHandle, (DWORD_PTR)affinityMask);

	if (priorMask == UINT64_C(0)) {
		// potentially do something with the error code in the future, but for now, this works.

		uint32_t errorCode = (uint32_t)GetLastError();

		return SetThreadAffinityMaskFailed;
	}

	if uint64_

	*appliedAffinitymask = appliedMask; // copy the current applied mask into the already allocated ptr to pass back to C#

	return Success;
}