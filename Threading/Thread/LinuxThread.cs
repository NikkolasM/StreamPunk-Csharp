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
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] out System.UInt64[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
            out System.UInt64 aamLength
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
             suppliedMaskLength: (ulong)affinityMask.Length, // DONT CAST TO System.UINT64, YOU WANT THE LSP TO BE PISSED OFF WHEN THIS CAST CONVERSION IS WRONG. THUS, CAST TO ULONG, WHICH THE LSP KNOWS IS THE SAME AS THAT OTHER TYPE.
             tid: out System.Int32 id,
             appliedAffinityMask: out System.UInt64[] aam,
             aamLength: out System.UInt64 _ 
             );

            // outcomeCode = 0 means success, anything else means something unexpected happened
            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid arg initialization",
                    -2 => "Failed to allocate cpu set",
                    -3 => "Real bit position too large",
                    -4 => "Failed to set affinity",
                    -5 => "Failed to get real number of cpus",
                    -6 => "Too many cpus",
                    -7 => "Failed to allocate real cpu set",
                    -8 => "Failed to get affinity",
                    -9 => "Failed to allocate comparison mask",
                    _ => "Unknown error",
                };

                throw new NativeCallException($"{message}. outcomeCode={outcomeCode}");
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
            // DONT CAST THESE TYPES. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
            tid = id;
            appliedAffinityMask = aam;
        }

        [LibraryImport("ResetAffinityLinux.so", EntryPoint = "ResetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int ResetAffinityUnsafe(
            out System.Int32 tid,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] out System.UInt64[] appliedAffinityMask, // latest .NET will dealloc the passed back heap for me, since it knows the size of the array, and the bits per element.
            out System.UInt64 aamLength
            );

        // Idempotent
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity(out int tid, out ulong[] appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.ResetAffinityUnsafe(
                tid: out System.Int32 id,
                appliedAffinityMask: out System.UInt64[] aam,
                aamLength: out System.UInt64 _ // because its just for a passed back property from the C so the CLR knows the length of 'appliedAffinityMask'
                );

            // outcomeCode = 0 means success, anything else means something unexpected happened
            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid arg initialization.",
                    -2 => "Failed to get real number of cpus.",
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
    public class Affinity
    {
        public readonly ulong[] affinityMask; // an arbitrarily long bitmask read from right to left
        public Affinity(ulong[] affinityMask) {
            this.affinityMask = new ulong[affinityMask.Length];

            for (int i = 0; i < affinityMask.Length; i++) this.affinityMask[i] = affinityMask[i];
        }
    }

    // for providing a wrapper around the Thread instance to allow both public and certain private methods to
    // be exposed to the underlying dotnet thread associated with the given streampunk thread instance, but not from outside
    // threads interacting with the physical streampunk thread class instance itself. Therefore, the wrapper can either be safely copied or stay on the stack.
    public readonly struct ThreadAPIProxy<StartState>
    {
        public readonly Func<System.Threading.Thread?> GetDotnetThread;
        public readonly Func<Affinity> GetAffinity;
        public readonly Action<Affinity> SetAffinity;
        public readonly Action ResetAffinity;
        public ThreadAPIProxy(
            Func<System.Threading.Thread?> GetDotnetThreadDelegate,
            Func<Affinity> GetAffinityDelegate,
            Action<Affinity> SetAffinityDelegate,
            Action ResetAffinityDelegate
            )
        {
            this.GetDotnetThread = GetDotnetThreadDelegate;
            this.GetAffinity = GetAffinityDelegate;
            this.SetAffinity = SetAffinityDelegate;
            this.ResetAffinity = ResetAffinityDelegate;
        }
    }

    // important so that the calling thread of 'Start()' can do a simple spinlock with a timeout.
    internal class BootstrapState
    {
        public bool IsBootstrapped;
        public BootstrapState() { this.SetIsBootstrapped(false); }
        public bool GetIsBootstrapped() { return Volatile.Read(ref this.IsBootstrapped); }
        public void SetIsBootstrapped(bool IsBootstrapped) { Volatile.Write(ref this.IsBootstrapped, IsBootstrapped); }

    }
    public class Thread<StartState>
    {
        private Affinity Affinity;
        private Affinity AppliedAffinity;
        private int Tid;
        private readonly System.Threading.Lock _AffinityTransactionLock;

        public readonly decimal TimeoutMs;

        private readonly CancellationTokenSource cts;
        private System.Threading.Thread? DotnetThread;

        private bool Starting;
        private bool StartingAsync;
        private bool Started;

        private bool IsDisposing;
        private readonly System.Threading.Lock _DisposingTransactionLock;

        private bool DisposedConfirmExecutionContext;
        private bool DisposedConfirmStart;
        private bool DisposedConfirmStartAsync;

        private bool IsDisposed;
        public Thread(Affinity Affinity, decimal TimeoutMs = 50m, CancellationTokenSource? cts = null)
        {
            this.TimeoutMs = TimeoutMs;

            this.Affinity = Affinity;
            this.AppliedAffinity = new Affinity([]);
            this.Tid = 0;

            // this will ensure that in the case the Dispose() method is called on this instance, it doesn't cancel
            // the CTS upstream that was injected into the constructor. Yet at the same time, if the upstream CTS calls a cancel,
            // this instance will also cancel.
            if (cts != null) this.cts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            else this.cts = new CancellationTokenSource();

            this.DotnetThread = null;

            this.Starting = false;
            this.StartingAsync = false;
            this.Started = false;

            this.IsDisposing = false;
            this._DisposingTransactionLock = new();

            this.DisposedConfirmExecutionContext = false;
            this.DisposedConfirmStart = false;
            this.DisposedConfirmStartAsync = false;

            this.IsDisposed = false;
        }

        public Affinity GetAffinity() { lock (this._AffinityTransactionLock) return Volatile.Read(ref this.Affinity); }

        private void SetAffinity(Affinity newAffinity)
        {
            lock (this._AffinityTransactionLock) { 
                int currTid = Volatile.Read(ref this.Tid);

                Native.SetAffinity(newAffinity.affinityMask, out int tidAppliedTo, out ulong[] aam);

                Volatile.Write(ref this.Affinity, newAffinity);
                Volatile.Write(ref this.AppliedAffinity, new Affinity(aam));
                Volatile.Write(ref this.Tid, tidAppliedTo);

                if (currTid != tidAppliedTo) throw new TidDoesNotMatchException($"currTid={currTid},tidAppliedTo={tidAppliedTo}");
            }
        }

        private void ResetAffinity()
        {
            lock (this._AffinityTransactionLock)
            {
                int currTid = Volatile.Read(ref this.Tid);

                Native.ResetAffinity(out int tidAppliedTo, out ulong[] aam);

                Affinity newAffinity = new(aam);

                Volatile.Write(ref this.Affinity, newAffinity);
                Volatile.Write(ref this.AppliedAffinity, newAffinity);
                Volatile.Write(ref this.Tid, tidAppliedTo);

                if (currTid != tidAppliedTo) throw new TidDoesNotMatchException($"currTid={currTid},tidAppliedTo={tidAppliedTo}");
            }
        }

        public Affinity GetAppliedAffinity() { lock (this._AffinityTransactionLock) return Volatile.Read(ref this.AppliedAffinity); }

        public int GetTid() { lock (this._AffinityTransactionLock) return Volatile.Read(ref this.Tid); }

        public CancellationTokenSource GetCancellationTokenSource() { return this.cts; }

        public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }

        public bool GetIsDisposed() { return Volatile.Read(ref this.IsDisposed); }

        public Task GetIsDisposedAsync(CancellationToken ct, decimal TimeoutMs = 0m)
        {
            return Task.Run(async () =>
            {
                // have to instantiate like this because the LSP doesn't recognize branches for initialization otherwise i.e. using an if statement with a body.
                long startTime = TimeoutMs > 0m ? Stopwatch.GetTimestamp() : 0l;
                decimal msToSeconds = TimeoutMs > 0m ? this.TimeoutMs / 1000m : 0m;
                long timeoutInTicks = TimeoutMs > 0m ? (long)(msToSeconds * ((decimal)Stopwatch.Frequency)) : 0l;

                while (true)
                {
                    if (TimeoutMs > 0m && ((Stopwatch.GetTimestamp() - startTime) >= timeoutInTicks)) throw new TimedOutException();

                    ct.ThrowIfCancellationRequested();

                    if (Volatile.Read(ref this.IsDisposed)) return;

                    // so that the thread as part of the TPL ThreadPool can work on other stuff instead of this leading to a deadlock. Here, we can utilize
                    // Task.Yield instead of System.Threading.Thread.Yield since AwaitIsDisposedAsync has softer requirements for propagating the change of
                    // this state to the user. At the same time, CLR visibility here allows this method to have many multiple Tasks it produces concurrently running
                    // with reasonable (but not super real-time) resolution latency. 
                    await Task.Yield();
                }
            }, ct);
        }

        public bool GetIsDisposing() { return Volatile.Read(ref this.IsDisposing); }

        private void ThrowIfShouldExit(CancellationToken ct)
        {
            if (this.GetIsDisposing()) throw new DisposingException();

            ct.ThrowIfCancellationRequested();
        }

        // Checks current state of the given Thread instance it's a part of to ensure that no existing running thread routine is happening.
        // Create a bootstrap state to be used to mediate shared memory flags between the thread that is to be created and teh consumer thread calling Start().
        // Within the body of the new thread created, the thread is pinned, which includes an unpin saga to ensure the underlying thread is reset to a valid state.
        // If bootstrap is successful, the thread will be pinned and begin executing the supplied routine with the supplied state.
        public void Start(StartState state, Action<StartState, ThreadAPIProxy<StartState>, CancellationToken> executionContext)
        {
            CancellationToken ct = this.cts.Token;

            try
            {
                    this.ThrowIfShouldExit(ct);

                if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists.");

                    this.ThrowIfShouldExit(ct);

                BootstrapState bss = new(); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                    this.ThrowIfShouldExit(ct);

                System.Threading.Thread DotnetThread = new(() =>
                {
                    System.Threading.Thread? thread = this.GetDotnetThread();

                    try
                    {
                        if (thread == null) throw new ThreadNotFoundException();

                            this.ThrowIfShouldExit(ct);

                        this.SetAffinity(this.Affinity);

                            this.ThrowIfShouldExit(ct);

                        bss.SetIsBootstrapped(true);

                            this.ThrowIfShouldExit(ct);
                    }
                    catch (Exception e)
                    {
                        this.ResetAffinity();

                        if (e is DisposingException || e is OperationCanceledException) return;

                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                            this.ThrowIfShouldExit(ct);

                        ThreadAPIProxy<StartState> proxy = new(this.GetDotnetThread, this.GetAffinity, this.SetAffinity, this.ResetAffinity);

                            this.ThrowIfShouldExit(ct);

                        executionContext(state, proxy, ct);

                            this.ThrowIfShouldExit(ct);

                        this.ResetAffinity(); // reset the affinity once the work is done. the code can exit out normally now without lingering side effects.
                    }
                    catch (Exception e)
                    {
                        if (e is TidDoesNotMatchException) throw new ThreadRuntimeException(null, e); // for the tid mismatch exception in the happy path.

                        this.ResetAffinity();

                        if (e is DisposingException || e is OperationCanceledException) return; // these exceptions are for handling whether the overall thread is cancelled or is being disposed.

                        throw new ThreadRuntimeException(null, e); // for all other types of exceptions that don't fit the criteria of the prior conditions.
                    }
                });

                    this.ThrowIfShouldExit(ct);

                Volatile.Write(ref this.DotnetThread, DotnetThread);

                    this.ThrowIfShouldExit(ct);

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                DotnetThread.IsBackground = true;

                    this.ThrowIfShouldExit(ct);

                DotnetThread.Start(); // synchronously registers the thread created as a root node in the CLR, so you don't have to worry about lifetime edge cases. 

                    this.ThrowIfShouldExit(ct);

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                // the underlying thread, but not the CLR. The CFS is your friend.

                long startTime = Stopwatch.GetTimestamp();
                decimal msToSeconds = this.TimeoutMs / 1000m;
                long timeoutInTicks = (long)(msToSeconds * ((decimal)Stopwatch.Frequency));

                while ((Stopwatch.GetTimestamp() - startTime) < timeoutInTicks)
                {
                        this.ThrowIfShouldExit(ct);

                    if (bss.GetIsBootstrapped()) return;

                    System.Threading.Thread.Yield();
                }

                this.cts.Cancel();

                throw new ThreadBootstrapException(null, new TimedOutException());
            }
            catch (Exception e)
            {
                if (e is DisposingException) lock (_DisposingTransactionLock) throw; // the reason for this lock, is so that the cts as part of the Dispose() method can have its Cancel() method invoked transactionally.
                
                if (e is OperationCanceledException) throw;

                throw new StartException(null, e);
            }
        }

        // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
        // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
        // Ensures that the lifetime between this class instance and the thread it's meant to encapsulate are maintained properly. For instance, this will allow 
        // not only async bootstrapping of the instance, but also the body of the task will retain routes to the original reference type args. so if the original caller
        // doesn't use these values again, the task itself prevents them from being GCed. This also includes the class instance too.
        public Task StartAsync(StartState state, Action<StartState, ThreadAPIProxy<StartState>, CancellationToken> executionContext)
        {
            CancellationToken ct = this.cts.Token;

            return Task.Run(() =>
            {
                try
                {
                        this.ThrowIfShouldExit(ct);

                    this.Start(state, executionContext);
                }
                catch (Exception e)
                {
                    if (e is DisposingException) Volatile.Write(ref this.IsDisposed, true); ct.ThrowIfCancellationRequested();

                    if (e is OperationCanceledException) ct.ThrowIfCancellationRequested(); // so the surrounding task can exit in the Cancelled state. Should be visible here, since cts.Cancel() uses interlocked under the hood, which it 

                    throw new StartAsyncException(null, e);
                }
            }, ct);
        }
        public void Dispose()
        {
            if (Volatile.Read(ref this.IsDisposing) || Volatile.Read(ref this.IsDisposed)) return;

            // having a lock here is important, so that both the write to the flag and the invocation of the cancellation is transactional from the POV of catch blocks in relevant code that looks at these flags.
            lock (_DisposingTransactionLock)
            {
                Volatile.Write(ref this.IsDisposing, true); // important so that both the sync and async Start() APIs can handle disposal gracefully/allow you to implement proper cleanup routines.

                // reusing the ct is simpler for both cancellations and disposals. This reduces how much is needed to be injected
                // into the execution context supplied while also being able to reuse the safe-exit logic that's made in the supplied execution context.
                this.cts.Cancel();
            }
        }
    }

    class TidDoesNotMatchException : Exception
    {
        public TidDoesNotMatchException() { }
        public TidDoesNotMatchException(string message) : base(message) { }
        public TidDoesNotMatchException(string? message, Exception innerException) : base(message, innerException) { }
    }
}
