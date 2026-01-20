    using StreamPunk.Threading.Thread.Errors;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    namespace StreamPunk.Threading.Thread.Linux
    {
        public partial class Native
        {
            // imports only happen when you actually invoke the method so this is fine.
            // The values passed back via 'out' arguments will auto cleanup the underlying 
            [LibraryImport("SetAffinityLinux.so", EntryPoint = "SetAffinityUnsafe")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
            public static partial int SetAffinityUnsafe(
                in System.UInt64[] suppliedAffinityMask,
                System.UInt64 suppliedMaskLength,
                out System.Int32 tid,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] // For passing an array on the heap allocated in the C back to the CLR safely. CLR needs to know the size of the arr. This is how. 
                out System.UInt64[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
                out System.UInt64 appliedMaskLength
                );

            public static void SetAffinity(ulong[] affinityMask, out int tid, out ulong[] appliedAffinityMask)
            {
                // Check the supplied args
                if (affinityMask.Length <= 0) throw new ArgumentException("Supplied affinity mask needs atleast one element.");

                bool hasAtleastOneMarkedCore = false;

                foreach (ulong element in affinityMask) if (element > 0UL) hasAtleastOneMarkedCore = true;

                if (!hasAtleastOneMarkedCore) throw new ArgumentException("Supplied affinity mask needs atleast one marked core.");

                // Once the args are validated, make the native call to pin the thread
                System.Int32 outcomeCode = Native.SetAffinityUnsafe(
                 suppliedAffinityMask: affinityMask,
                 suppliedMaskLength: (System.UInt64)affinityMask.Length,
                 tid: out System.Int32 id,
                 appliedAffinityMask: out System.UInt64[] aam,
                 appliedMaskLength: out System.UInt64 _ // because its just for a passed back property from the C so the CLR knows the length of 'appliedAffinityMask'
                 );

                // outcomeCode = 0 means success, anything else means something unexpected happened
                if (outcomeCode < 0)
                {
                    string message = outcomeCode switch
                    {
                        -1 => "Invalid arg initialization.",
                        -2 => "Failed to allocate cpu set.",
                        -3 => "Real bit position too large.",
                        -4 => "Failed to set affinity.",
                        -5 => "Failed to get real number of cpus.",
                        -6 => "Too many cpus.",
                        -7 => "Failed to allocate real cpu set.",
                        -8 => "Failed to get affinity",
                        -9 => "Failed to allocate comparison mask",
                        _ => "Unknown error.",
                    };

                    throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
                }

                if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

                // Check to ensure that a valid tid was written back from the native context.
                if (id <= 0) throw new InvalidTidException($"tid={id}");

                // Check to see if the supplied and applied masks are the same.
                // the affinity mask can be longer, all that's being checked is whether what's actually applied is a matching subset of what's supplied.
                // iterate from right to left, and make raw number comparisons.
                // No need to check if the real cpu subset of affinityMask is all 0's or not, the Linux kernel will return an error in such
                // cases automatically through sched_setaffinity().
                for (int i = affinityMask.Length - 1; i >= 0; i--)
                {
                    int aamIndex = (aam.Length - 1) - (affinityMask.Length - 1 - i);

                    if (aamIndex >= 0 && affinityMask[i] != aam[aamIndex]) throw new AppliedMaskMismatchException($"i={i},aamIndex={aamIndex},affinityMask[i]={affinityMask[i]},aam[aamIndex]={aam[aamIndex]}");
                }

                // you still apply the mask as the end for the user to see, because Linux can sometimes drop bits in the mask even though it's successful. i.e. if cgroups exist. 
                // Also, DONT CAST THESE TYPES. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
                tid = id;
                appliedAffinityMask = aam;
            }

            [LibraryImport("ResetAffinityLinux.so", EntryPoint = "ResetAffinityUnsafe")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
            public static partial int ResetAffinityUnsafe(
                out System.Int32 tid,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] // For passing an array on the heap allocated in the C back to the CLR safely. CLR needs to know the size of the arr. This is how. 
                out System.UInt64[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
                out System.UInt64 appliedMaskLength
                );

            // Idempotent
            // Just sets the given thread affinity to 1 for every physically available CPU.
            public static void ResetAffinity(out int tid, out ulong[] appliedAffinityMask)
            {
                System.Int32 outcomeCode = Native.ResetAffinityUnsafe(
                    tid: out System.Int32 id,
                    appliedAffinityMask: out System.UInt64[] aam,
                    appliedMaskLength: out System.UInt64 _ // because its just for a passed back property from the C so the CLR knows the length of 'appliedAffinityMask'
                    );

                // outcomeCode = 0 means success, anything else means something unexpected happened
                if (outcomeCode < 0)
                {
                    string message = outcomeCode switch
                    {
                        -1 => "Invalid arg initialization.",
                        -2 => "Failed to get real number of cpust.",
                        -3 => "Too many cpus.",
                        -4 => "Failed to allocate cpu set.",
                        -5 => "Failed to set affinity",
                        -6 => "Failed to allocate comparison cpu set.",
                        -7 => "Failed to get affinity.",
                        -8 => "Failed to allocate comparison mask.",
                        _ => "Unknown error.",
                    };

                    throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
                }

                if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

                // DONT CAST THESE TYPES. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
                tid = id;
                appliedAffinityMask = aam;
            }
        }

        // important so that the calling thread of 'Start()' can do a simple spinlock with a timeout.
        internal class BootstrapState
        {
            public bool isBootstrapped;
            public bool hasFailed;
            public bool GetIsBootstrapped()
            {
                return Volatile.Read(ref this.isBootstrapped); 
            }
            public void SetIsBootstrapped(bool IsBootstrapped)
            {
                Volatile.Write(ref this.isBootstrapped, IsBootstrapped);
            }
            public bool GetHasFailed()
            {
                return Volatile.Read(ref this.hasFailed);
            }
            public void SetHasFailed(bool HasFailed)
            {
                Volatile.Write(ref this.hasFailed, HasFailed);
            }
            public BootstrapState()
            {
                this.SetIsBootstrapped(false);
                this.SetHasFailed(false);
            }
        }
        public class Affinity 
        {
            // an arbitrarily long bitmask read from right to left
            public readonly ulong[] affinityMask;
            public Affinity(ulong[] affinityMask)
            {
                this.affinityMask = affinityMask;
            }
        }
        public class Thread<StartState>
        {
            public readonly Affinity affinity;
            public readonly long timeoutMs;

            private int tid;
            private System.Threading.Thread? DotnetThread;
            public Thread(Affinity affinity, long timeoutMs = 100L)
            {
                this.affinity = affinity;
                this.timeoutMs = timeoutMs;
                this.SetTid(0);
                this.SetDotnetThread(null);
            }
            public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }
            public void SetDotnetThread(System.Threading.Thread? DotnetThread) { Volatile.Write(ref this.DotnetThread, DotnetThread); }
            public int GetTid() { return Volatile.Read(ref this.tid); }
            public void SetTid(int tid) { Volatile.Write(ref this.tid, tid); }

            // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
            // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
            // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
            // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
            public void Start(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;  

                        if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists.");

                        if (ct.IsCancellationRequested) return;

                        BootstrapState bss = new (); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                        if (ct.IsCancellationRequested) return;

                        System.Threading.Thread DotnetThread = new (() =>
                        {
                            System.Threading.Thread? thread = this.GetDotnetThread();

                            try
                            {
                                if (ct.IsCancellationRequested || bss.GetHasFailed()) return;

                                if (thread == null) throw new ThreadNotFoundException("thread=null");

                                if (ct.IsCancellationRequested || bss.GetHasFailed()) return;

                                Native.SetAffinity(this.affinity.affinityMask, out int tid, out ulong[] _);

                                if (ct.IsCancellationRequested || bss.GetHasFailed()) { Native.ResetAffinity(out int _, out ulong[] _); return; }

                                this.SetTid(tid);

                                if (ct.IsCancellationRequested || bss.GetHasFailed()) { Native.ResetAffinity(out int _, out ulong[] _); return; }

                                bss.SetIsBootstrapped(true);

                                if (ct.IsCancellationRequested || bss.GetHasFailed()) { Native.ResetAffinity(out int _, out ulong[] _); return; }
                            }
                            catch (Exception e)
                            {
                                Native.ResetAffinity(out int _, out ulong[] _);
                                throw new ThreadBootstrapException(null, e);
                            }

                            try
                            {
                                if (ct.IsCancellationRequested || bss.GetHasFailed()) { Native.ResetAffinity(out int _, out ulong[] _); return; }

                                executionContext(state, thread, ct);
                            }
                            catch (Exception e)
                            {
                                Native.ResetAffinity(out int _, out ulong[] _);
                                throw new ThreadRuntimeException(null, e);
                            }
                        });

                        this.SetDotnetThread(DotnetThread);

                        if (ct.IsCancellationRequested) return;

                        // need to make it a background thread, since its a foreground thread by default. 
                        // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                        // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                        // if this called method returns without exceptions.
                        DotnetThread.IsBackground = true;
                        DotnetThread.Start(); // synchronously registers the thread created as a root node in the CLR, so you don't have to worry about lifetime edge cases. 

                        // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                        // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                        // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                        // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                        // the underlying thread, but not the CLR. The CFS is your friend.
                        Stopwatch sw = new ();
                        sw.Start();

                        while (sw.ElapsedMilliseconds < this.timeoutMs) if (ct.IsCancellationRequested || bss.GetIsBootstrapped()) return;

                        bss.SetHasFailed(true);

                        throw new ThreadBootstrapException("Timed out.");

                    }
                    catch (Exception e)
                    {
                        throw new StartException(null, e);
                    }
            }

            // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
            // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
            // Ensures that the lifetime between this class instance and the thread it's meant to encapsulate are maintained properly. For instance, this will allow 
            // not only async bootstrapping of the instance, but also the body of the task will retain routes to the original reference type args. so if the original caller
            // doesn't use these values again, the task itself prevents them from being GCed. This also includes the class instance too.
            public Task StartAsync(StartState state, Action<StartState, System.Threading.Thread, CancellationToken> executionContext, CancellationToken ct)
            {
                return Task.Run(() =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();

                        this.Start(state, executionContext, ct);

                        ct.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Throw directly so that the given task can transition to the Cancelled state properly
                    }
                    catch (Exception e)
                    {
                        throw new StartAsyncException(null, e);
                    }
                }, ct);
            }
        }
    }
