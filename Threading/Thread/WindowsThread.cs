using StreamPunk.Threading.Thread.Errors;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StreamPunk.Threading.Thread.Windows
{
    public partial class Native
    {
        // imports only happen when you actually invoke the method so this is fine.
        // The values passed back via 'out' arguments will auto cleanup the underlying 
        [LibraryImport("SetAffinityWindows.dll", EntryPoint = "SetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int SetAffinityUnsafe(
            System.UInt64 suppliedAffinityMask,
            out System.UInt64 appliedAffinityMask
        );

        public static void SetAffinity(ulong suppliedAffinityMask, out ulong appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.SetAffinityUnsafe(suppliedAffinityMask, out System.UInt64 aam);  // DONT CAST THE ARG TYPES. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT

            if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid argument initialization.",
                    -2 => "Failed to get handle.",
                    -3 => "Failed to set thread affinity mask.",
                    -4 => "Applied affinity mask does not match.",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
            }

            appliedAffinityMask = aam; // DONT CAST THE TYPE. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
        }

        [LibraryImport("ResetAffinityWindows.dll", EntryPoint = "ResetAffinityUnsafe")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        public static partial int ResetAffinityUnsafe(out System.UInt64 appliedAffinityMask);

        // Idempotent
        // Just sets the given thread affinity to 1 for every physically available CPU.
        public static void ResetAffinity(out ulong appliedAffinityMask)
        {
            System.Int32 outcomeCode = Native.ResetAffinityUnsafe(out System.UInt64 aam);

            if (outcomeCode > 0) throw new NativeCallException($"Unknown outcome code. outcomeCode={outcomeCode}");

            if (outcomeCode < 0)
            {
                string message = outcomeCode switch
                {
                    -1 => "Invalid argument initialization.",
                    -2 => "Failed to get handle.",
                    -3 => "Failed to set thread affinity mask.",
                    -4 => "Applied affinity mask does not match.",
                    _ => "Unknown error.",
                };

                throw new NativeCallException($"{message} outcomeCode={outcomeCode}");
            }

            appliedAffinityMask = aam; // DONT CAST THE TYPE. YOU WANT THE LSP TO BE PISSED OFF WHEN THEYRE DIFFERENT
        }
    }
    public class Affinity
    {
        public readonly ulong affinityMask; // an arbitrarily long bitmask read from right to left
        public Affinity(ulong affinityMask) { this.affinityMask = affinityMask; }
    }

    // for providing a wrapper around the Thread instance to allow both public and certain private methods to
    // be exposed to the underlying dotnet thread associated with the given streampunk thread instance, but not from outside
    // threads interacting with the physical streampunk thread class instance itself. Therefore, the wrapper can either be safely copied or stay on the stack.
    public struct ThreadAPIProxy<StartState>
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

    // important so that the calling thread of 'Start()' can do a simple predictable spinlock with a timeout.
    internal class BootstrapStateMachine
    {
        private bool IsBootstrapped = false;
        public bool GetIsBootstrapped() { return Volatile.Read(ref this.IsBootstrapped); }
        public void SetIsBootstrappedToTrue() { Volatile.Write(ref this.IsBootstrapped, true); }
    }

    internal class DisposeStateMachine()
    {
        private bool UsingStartAsync = false;
        private bool DotnetThreadStarted = false; // important so that if an error is thrown within Start() before DotnetThread.Start() is called, the entire instance can gracefully clean up.
        private bool DotnetThreadExited = false;
        private bool StartExited = false;
        private bool StartAsyncExited = false;
        private bool IsDisposing = false;
        private bool IsDisposed = false;

        private readonly Lock _DisposingToDisposedTransactionLock = new();

        public bool GetUsingStartAsync() { return Volatile.Read(ref this.UsingStartAsync); }
        public void SetUsingStartAsyncToTrue() { Volatile.Write(ref this.UsingStartAsync, true); }

        public bool GetDotnetThreadStarted() { return Volatile.Read(ref this.DotnetThreadStarted); }
        public void SetDotnetThreadStartedToTrue() { Volatile.Write(ref this.DotnetThreadStarted, true); }

        public bool GetDotnetThreadExited() { return Volatile.Read(ref this.DotnetThreadExited); }
        public void SetDotnetThreadExitedToTrue() { Volatile.Write(ref this.DotnetThreadExited, true); }

        public bool GetStartExited() { return Volatile.Read(ref this.StartExited); }
        public void SetStartExitedToTrue() { Volatile.Write(ref this.StartExited, true); }

        public bool GetStartAsyncExited() { return Volatile.Read(ref this.StartAsyncExited); }
        public void SetStartAsyncExitedToTrue() { Volatile.Write(ref this.StartAsyncExited, true); }

        public bool GetIsDisposing() { lock (_DisposingToDisposedTransactionLock) return Volatile.Read(ref this.IsDisposing); }
        public void SetIsDisposingToTrue() { Volatile.Write(ref this.IsDisposing, true); }

        public bool GetIsDisposed() { lock (_DisposingToDisposedTransactionLock) return Volatile.Read(ref this.IsDisposed); }
        public void AttemptFinalizeDispose()
        {
            // IsReadyToFinalize = true if all the conditions below are met:
            // - Start method have exited
            // - If the Dotnet thread execution body either exited or was never started in the first place
            // - If called using StartAsync, if the execution context + the start method + the task returned by StartAsync have exited
            bool isReadyToFinalizeDisposal =
               (!Volatile.Read(ref this.DotnetThreadStarted) || Volatile.Read(ref this.DotnetThreadExited)) &&
                Volatile.Read(ref this.StartExited) &&
                (!Volatile.Read(ref this.UsingStartAsync) || Volatile.Read(ref this.StartAsyncExited));

            if (isReadyToFinalizeDisposal)
            {
                // lock important for ordering guarantees when calling in Dispose() while a dispose is already occuring on the instance.
                lock (this._DisposingToDisposedTransactionLock)
                {
                    Volatile.Write(ref this.IsDisposing, false);
                    Volatile.Write(ref this.IsDisposed, true);
                }
            }
        }
    }

    public class Thread<StartState>
    {
        public readonly decimal TimeoutMs;

        private Affinity Affinity;
        private Affinity AppliedAffinity = new Affinity(0x0);
        private readonly Lock _AffinityTransactionLock = new();

        private CancellationTokenSource cts;

        private System.Threading.Thread? DotnetThread = null;

        private readonly DisposeStateMachine _DisposeStateMachine = new();
        private readonly Lock _DisposeInitTransactionLock = new();

        public Thread(Affinity affinity, decimal timeoutMs = 50m, CancellationTokenSource? cts = null)
        {
            this.TimeoutMs = timeoutMs;
            this.Affinity = affinity;

            // this will ensure that in the case the Dispose() method is called on this instance, it doesn't cancel
            // the CTS upstream that was injected into the constructor. Yet at the same time, if the upstream CTS calls a cancel,
            // this instance will also cancel.
            this.cts = cts != null ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token) : new CancellationTokenSource();
        }

        public Affinity GetAffinity() { lock (this._AffinityTransactionLock) return Volatile.Read(ref this.Affinity); }

        // to be called by the thread itself within its execution context. SHOULD NOT BE CALLED FROM EXTERNAL SOURCES.
        // IF YOU WANT TO BE ABLE TO SET AFFINITY FROM AN OUTSIDE THREAD, PLEASE MAKE YOUR OWN OBSERVABLE THAT YOU INJECT AND CHECK
        // COOPERATIVELY WITHIN THE EXECUTION CONTEXT YOU SUPPLY.
        // Lastly, keep an eye on this method usage, rapid affinity repinning could potentialy blow up the heap if you are careless.
        private void SetAffinity(Affinity affinity)
        {
            lock (this._AffinityTransactionLock)
            {
                Native.SetAffinity(affinity.affinityMask, out ulong aam);

                Volatile.Write(ref this.Affinity, affinity);
                Volatile.Write(ref this.AppliedAffinity, new Affinity(aam));
            }
        }

        private void ResetAffinity()
        {
            lock (this._AffinityTransactionLock)
            {
                Native.ResetAffinity(out ulong aam);

                Affinity newAffinity = new(aam);

                Volatile.Write(ref this.Affinity, newAffinity);
                Volatile.Write(ref this.AppliedAffinity, newAffinity);
            }
        }

        public Affinity GetAppliedAffinity() { return Volatile.Read(ref this.AppliedAffinity); }
      
        public CancellationTokenSource GetCancellationTokenSource() { return Volatile.Read(ref this.cts); }

        public System.Threading.Thread? GetDotnetThread() { return Volatile.Read(ref this.DotnetThread); }

        public bool GetIsDisposing() { lock (this._DisposeInitTransactionLock) return this._DisposeStateMachine.GetIsDisposing(); }

        public bool GetIsDisposed() { lock (this._DisposeInitTransactionLock) return this._DisposeStateMachine.GetIsDisposed(); }

        public Task AwaitIsDisposedAsync(CancellationToken ct, decimal TimeoutMs = 0m)
        {
            return Task.Run(async () =>
            {
                long startTime = TimeoutMs > 0m ? Stopwatch.GetTimestamp() : 0l;
                decimal msToSeconds = TimeoutMs > 0m ? this.TimeoutMs / 1000m : 0m;
                long timeoutInTicks = TimeoutMs > 0m ? (long)(msToSeconds * ((decimal)Stopwatch.Frequency)) : 0l;

                while (true)
                {
                    if (TimeoutMs > 0m && (Stopwatch.GetTimestamp() - startTime) >= timeoutInTicks) throw new TimedOutException();

                    ct.ThrowIfCancellationRequested();

                    if (this._DisposeStateMachine.GetIsDisposed()) return;

                    // so that the thread as part of the TPL ThreadPool can work on other stuff instead of this leading to a deadlock. Here, we can utilize
                    // Task.Yield instead of System.Threading.Thread.Yield since AwaitIsDisposedAsync has softer requirements for propagating the change of
                    // this state to the user. At the same time, CLR visibility here allows this method to have many multiple Tasks it produces concurrently running
                    // with reasonable (but not super real-time) resolution latency. 
                    await Task.Yield();
                }
            }, ct);
        }

        private void ThrowIfShouldExit(CancellationToken ct)
        {
            if (this._DisposeStateMachine.GetIsDisposing()) throw new DisposingException();

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
                if (this.GetDotnetThread() != null) throw new ThreadStateException($"Thread already exists."); this.ThrowIfShouldExit(ct);

                BootstrapStateMachine bsm = new(); this.ThrowIfShouldExit(ct); // to capture the bootstrap state inside the closure so that the calling thread of 'Start()' can spinlock on such

                System.Threading.Thread DotnetThread = new(() =>
                {
                    this._DisposeStateMachine.SetDotnetThreadStartedToTrue();

                    System.Threading.Thread? thread = this.GetDotnetThread();

                    try
                    {
                        if (thread == null) throw new ThreadNotFoundException(); this.ThrowIfShouldExit(ct);

                        this.SetAffinity(this.Affinity); this.ThrowIfShouldExit(ct);

                        bsm.SetIsBootstrappedToTrue(); this.ThrowIfShouldExit(ct);
                    }
                    catch (Exception e)
                    {
                        this.ResetAffinity(); // doesn't need any additional handling since this native wrapper should throw when the aam doesn't match the max value of ulong 

                        // will set IsDisposed=true if this context is the last context to exit out. 
                        if (e is DisposingException)
                        {
                            lock (this._DisposeInitTransactionLock)
                            {
                                this._DisposeStateMachine.SetDotnetThreadExitedToTrue();
                                this._DisposeStateMachine.AttemptFinalizeDispose();
                                return;
                            }
                        }

                        if (e is OperationCanceledException) return;

                        throw new ThreadBootstrapException(null, e);
                    }

                    try
                    {
                        this.ThrowIfShouldExit(ct);

                        ThreadAPIProxy<StartState> proxy = new(this.GetDotnetThread, this.GetAffinity, this.SetAffinity, this.ResetAffinity); this.ThrowIfShouldExit(ct);

                        executionContext(state, proxy, ct); this.ThrowIfShouldExit(ct);

                        this.ResetAffinity(); // reset the affinity once the work is done. the code can exit out normally now without lingering side effects.
                    }
                    catch (Exception e)
                    {
                        this.ResetAffinity(); // doesn't need any additional handling since this native wrapper should throw when the aam doesn't match the max value of ulong 

                        // will set IsDisposed=true if this context is the last context to exit out. 
                        if (e is DisposingException)
                        {
                            lock (this._DisposeInitTransactionLock)
                            {
                                this._DisposeStateMachine.SetDotnetThreadExitedToTrue();
                                this._DisposeStateMachine.AttemptFinalizeDispose();
                                return;
                            }
                        }

                        if (e is OperationCanceledException) return; // these exceptions are for handling whether the overall thread is cancelled or is being disposed.

                        throw new ThreadRuntimeException(null, e);
                    }
                });

                this.ThrowIfShouldExit(ct);

                // need to make it a background thread, since its a foreground thread by default. 
                // this will ensure that exceptions via timeouts properly shut down the entire process, rather than having a thread 
                // keep things hanging. The caller can decide to change the thread back to being a foreground thread rather than a background thread
                // if this called method returns without exceptions.
                DotnetThread.IsBackground = true; this.ThrowIfShouldExit(ct);

                Volatile.Write(ref this.DotnetThread, DotnetThread); this.ThrowIfShouldExit(ct);

                DotnetThread.Start(); this.ThrowIfShouldExit(ct);

                // use a spin lock to monitor the thread bootstrapping. This is the sync API, so spinlocking this way works and allows
                // instant an accurate timeouts. Removes the fuss related to rescheduling different threads from the .NET ThreadPool which may
                // not be updated on the volatile members as consistently. The kernel underneath should schedule out the underlying thread so that
                // its not locked on just this CPU bound task here. Don't try to guess in the C# what the kernel wants, just let the kernel reschedule
                // the underlying thread, but not the CLR. The CFS is your friend.

                long startTime = Stopwatch.GetTimestamp();
                decimal msToSeconds = this.TimeoutMs / 1000m;
                long timeoutInTicks = (long)(msToSeconds * ((decimal)Stopwatch.Frequency));

                // so that the thread as part of the TPL ThreadPool can work on other stuff instead of this leading to a deadlock.
                // Uses thread yield instead of task yield because on its own, this isn't a task but can be the executing body of a task. This will
                // allow the OS to allow any other tasks on the same core of the underlying kernel thread executing this chain to run. In the managed side of things,
                // this keeps the CLR from preempting the loop itself. This ensures that CPU-bound noisy neighbors be it in the same process or in the same OS context
                // don't hang this thread up. However, since this isn't CLR-visible, in situations where you create many of these threads at once, it can potentially 
                // hit the threadpool max. however, since the happy path should resolve in microseconds, with millisecond-level timeouts, these should be very deterministic
                // even though they're async. 
                while ((Stopwatch.GetTimestamp() - startTime) < timeoutInTicks)
                {
                    this.ThrowIfShouldExit(ct);

                    if (bsm.GetIsBootstrapped()) return;

                    System.Threading.Thread.Yield();
                }

                if (this._DisposeStateMachine.GetIsDisposing())
                {
                    lock (this._DisposeInitTransactionLock)
                    {
                        this._DisposeStateMachine.SetStartExitedToTrue();
                        this._DisposeStateMachine.AttemptFinalizeDispose();
                    }
                }
                else
                {
                    this.cts.Cancel(); // to ensure that if the thread instance were to ever bootstrap after, it can quickly exit out via the cancellation.

                    throw new ThreadBootstrapException(null, new TimedOutException());
                }
            }
            catch (Exception e)
            {
                if (e is DisposingException)
                {
                    lock (this._DisposeInitTransactionLock)
                    {
                        this._DisposeStateMachine.SetStartExitedToTrue();
                        this._DisposeStateMachine.AttemptFinalizeDispose();
                        throw;
                    }
                }

                if (e is OperationCanceledException) throw;

                throw new StartException(null, e);
            }
        }

        // for encapsulating a synchronous operation to bootstrap a new pinned thread executing a particular routine.
        // using a task to encapsulate the entire routine, so that the given task thread can retain its context on the particular bootstrapping routine.
        public Task StartAsync(StartState state, Action<StartState, ThreadAPIProxy<StartState>, CancellationToken> executionContext)
        {
            CancellationToken ct = this.cts.Token;

            this._DisposeStateMachine.SetUsingStartAsyncToTrue();

            return Task.Run(() =>
            {
                try
                {
                    this.ThrowIfShouldExit(ct);

                    this.Start(state, executionContext); this.ThrowIfShouldExit(ct);
                }
                catch (Exception e)
                {
                    if (e is DisposingException)
                    {
                        lock (this._DisposeInitTransactionLock)
                        {
                            this._DisposeStateMachine.SetStartExitedToTrue();
                            this._DisposeStateMachine.AttemptFinalizeDispose();
                            throw new OperationCanceledException(null, e);
                        }
                    }

                    if (e is OperationCanceledException) throw; // so the surrounding task can exit in the Cancelled state. Should be visible here, since cts.Cancel() uses interlocked under the hood, which it 

                    throw new StartAsyncException(null, e);
                }
            }, ct);
        }

        public void Dispose()
        {
            if (this._DisposeStateMachine.GetIsDisposing() || this._DisposeStateMachine.GetIsDisposed()) return;

            // having a lock here is important, so that both the write to the flag and the invocation of the cancellation is transactional from the POV of catch blocks in relevant code that looks at these flags.
            lock (this._DisposeInitTransactionLock)
            {
                if (this._DisposeStateMachine.GetIsDisposing()) return;

                this._DisposeStateMachine.SetIsDisposingToTrue();

                // reusing the ct is simpler for both cancellations and disposals. This reduces how much is needed to be injected
                // into the execution context supplied while also being able to reuse the safe-exit logic that's made in the supplied execution context.
                this.cts.Cancel();
            }
        }
    }

    public class AppliedAffinityMaskDoesNotMatchException : Exception
    {
        public AppliedAffinityMaskDoesNotMatchException() { }   
        public AppliedAffinityMaskDoesNotMatchException(string message) : base(message) { }
        public AppliedAffinityMaskDoesNotMatchException(string? message, Exception? innerException) : base(message, innerException) { }
    }
}
