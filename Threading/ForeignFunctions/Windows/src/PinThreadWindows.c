#include <windows.h>

__declspec(dllexport)
unsigned int PinThreadWindows(unsigned long long affinityMask) {
	// DO NOT DEALLOC. 
	// sets a long to some negative number then casts it so that it's read
	// as a ptr address. That ptr address doesn't eval to anything, it's literally reading the address raw.
	// ensure the value of this address equates to -2.
	HANDLE currThreadHandle = GetCurrentThread(); 

	// DWORD_PTR ISNT ACTUALLY A POINTER. WEIRD MICROSOFT STUFF.
	// Will either return the prior mask as a ulong 64bit, or return 0 which signals an error.
	unsigned long long priorMask = (unsigned long long)SetThreadAffinityMask(currThreadHandle, (DWORD_PTR)affinityMask);  

	if (priorMask == 0) {
		unsigned int errorCode = (unsigned int)GetLastError();

		return errorCode;
	}

	unsigned int successCode = 0;

	return successCode;
}