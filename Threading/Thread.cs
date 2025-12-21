using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamPunk.Threading
{
    // Data structure to match the API of  sched_setaffinity() in libc
    public struct LinuxThread
    {
        // The pid (thread ID) will be fetched from within the execution context within the lib function being called

        // an arbitrarily long bitmask
        public readonly ulong[] affinityMask; 
        public LinuxThread(ulong[] affinityMask)
        {
            this.affinityMask = affinityMask;
        }
    }
    internal partial struct LinuxNative 
    {
        // imports only happen when you actually invoke the method so this is fine.
        [LibraryImport("PinThreadLinux.so")]
        public static partial int PinThreadLinux(in ulong[] affinityMask, uint maskLength, out int tid);
    }

    // Data structure to match the API of SetThreadAffinityMask() in win32
    public struct WindowsThread
    {
        // The handle (thread ID) will be fetched from within the execution context within the lib function being called

        // Windows double word for a bit mask
        public readonly ulong affinityMask;
        public WindowsThread(ulong affinityMask)
        {
            this.affinityMask = affinityMask;
        }
    }
    internal partial struct WindowsNative {

        // imports only happen when you actually invoke the method so this is fine.
        [LibraryImport("PinThreadWindows.dll")]
        public static partial int PinThreadWindows(ulong affinityMask);
    }

    // Uses struct by design to minimize the GC mark-and-sweep fanout.
    // Even though it holds a piece of heap, that's by design. The Thread struct.
    // is part of the heap now, but the other structs are avoided.
    // copying the struct SHOULD copy the thread reference.
    struct Thread<StartContext>
    {
        private System.Threading.Thread? SystemThread;

        // These are public to simplify testing
        public readonly LinuxThread? LinuxThread;
        public readonly WindowsThread? WindowsThread;

        // *** CONSTRUCTORS ***

        // What you would use in prod if on a Linux-based system
        public Thread(LinuxThread thread)
        {
            this.LinuxThread = thread;
            this.WindowsThread = null;
        }

        // What you would use in prod if on a Windows-based system
        public Thread(WindowsThread thread)
        {
            this.LinuxThread = null;
            this.WindowsThread = thread;
        }

        // Used for more advanced usecases on Windows-based systems + testing
        // The action syscall will wrap the actual call to your .dll or .so of choice.
        // Doesn't accept or return anything, so you have to create your own closure that encapsulates everything.
        // USE AT YOUR OWN RISK.
        public Thread()
        {
            this.LinuxThread = null;
            this.WindowsThread = null;
        }

        // *** PRIVATE APIs ***

        private System.Threading.Thread? GetThreadInstance()
        {
            return this.SystemThread;
        }

        // returns the thread ID of the calling thread
        private int PinThreadLinux(ulong[] affinityMask)
        {
            if (affinityMask.Length == 0) throw new AffinityMaskEmpty();

            int outcomeCode = LinuxNative.PinThreadLinux(affinityMask, (uint)affinityMask.Length, out int threadId);

            // TODO: add error handling based on the returned code from the native lib call.

            // threadId (pid) can't be 0 or negative. <= 0 represents a range of reserved values
            if (threadId <= 0)
            {
                throw new InvalidThreadIdFetched($"Thread ID (tid) for Linux cannot be 0 or negative. threadId={threadId}");
            }

            return threadId;
        }

        private void PinThreadWindows(ulong affinityMask)
        {
            int outcomeCode = WindowsNative.PinThreadWindows(affinityMask);

            // add error handling based on the returned code from the native lib call.
        }
        
        private void StartLinux()
        {
        
        }

        private void StartWindows()
        {

        }

        // *** PUBLIC APIs *** 

        // For starting the thread preconfigured for Windows or Linux.
        //
        // Allowed to reuse the Thread instance if the underlying system thread fails for some reason.
        // Just ensure that you resupply the lambda representing the Thread execution context (start) along with 
        // the start object.
        public int Start(StartContext context, Action<StartContext, System.Threading.Thread> start)
        {
            // To ensure that start can only execute if the existing thread (if any) isn't running currently.
            if (this.SystemThread != null)
            {
                ThreadState threadState = this.SystemThread.ThreadState;
                bool isWaitSleepJoin = threadState == ThreadState.WaitSleepJoin;
                bool isRunning = threadState == ThreadState.Running;

                if (isRunning || isWaitSleepJoin)
                {
                    throw new ThreadStateException($"Invalid ThreadState for Start(). ThreadState={threadState}");
                }
            }

            // To fix context issues in the lambda below. Only one of these values will be not-null, since 
            // there are dedicated constructors for each above.
            LinuxThread? linuxThread = this.LinuxThread;
            WindowsThread? windowsThread = this.WindowsThread;

            // To fix the context issues in the lambda below. Creates a closure
            Func<System.Threading.Thread?> getThread = this.GetThreadInstance;

            // for either Linux (pid) or Windows (thread ID) based on what you chose.
            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            this.SystemThread = new System.Threading.Thread(() => { 
                try
                {
                    // To make sure the VM doesn't move work onto different kernel threads, even though
                    // we are pinning the kernel thread in the given scenario. 
                    System.Threading.Thread.BeginThreadAffinity();

                    // The lambda represents the execution context of
                    // the new thread. The goal here is to give that execution context access to its own thread class instance
                    // so it can invoke its methods to create lifecycles. At the same time, it applies strong encapsulation
                    System.Threading.Thread? currContextThread = getThread();

                    if (currContextThread == null)
                    {
                        throw new FailedToGetThreadException("currContextThread=null");
                    }

                    if (linuxThread != null)
                    {
                        // execute the LibraryImport method for thread pinning on Linux with a supplied callback to allow writing the thread ID (pid) to 'threadId'


                    }
                    else if (windowsThread != null)
                    {
                        // execute the LibraryImport method for thread pinning on Windows with a supplied callback to allow writing the thread ID to 'threadId'
                    }
                    else
                    {
                        throw new FailedToPinThreadException("No valid thread pinning routine.");
                    }

                    start(context, currContextThread);

                    System.Threading.Thread.EndThreadAffinity();
                } catch (Exception e)
                {
                    System.Threading.Thread.EndThreadAffinity();

                    throw new ThreadRuntimeException(message: "Thread runtime exception.", innerException: e);
                }
            });

            this.SystemThread.Start();

            return threadId;
        }

        // Used for more advanced use cases 
        // Doesn't accept or return anything, so you have to create your own closure that encapsulates everything.
        // ************USE AT YOUR OWN RISK.
        public void Start(StartContext context, Action<StartContext, System.Threading.Thread> start, Action threadPinningStrategy) {

        }

    }
    class ThreadStateException : System.Threading.ThreadStateException
    {
        public ThreadStateException() { }
        public ThreadStateException(string message) : base(message) { }
    }
    class ThreadRuntimeException : Exception
    {
        public ThreadRuntimeException() { }
        public ThreadRuntimeException(string message) : base(message) { }
        public ThreadRuntimeException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
    class FailedToPinThreadException : ThreadRuntimeException 
    { 
        public FailedToPinThreadException() { }
        public FailedToPinThreadException(string message) : base(message) { }
    }
    class FailedToGetThreadException : ThreadRuntimeException
    {
        public FailedToGetThreadException() { }
        public FailedToGetThreadException(string message) : base(message) { }
    }

    class InvalidThreadIdFetched : ThreadRuntimeException 
    {
        public InvalidThreadIdFetched() { }
        public InvalidThreadIdFetched(string message) : base(message) { }
    }

    class AffinityMaskEmpty : ThreadRuntimeException
    {
        public AffinityMaskEmpty() { }
        public AffinityMaskEmpty(string? message) : base(message) { }
    }
}
