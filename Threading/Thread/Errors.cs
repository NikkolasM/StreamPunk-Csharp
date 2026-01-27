using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamPunk.Threading.Thread.Errors
{
    class NativeCallException : Exception
    {
        public NativeCallException() { }
        public NativeCallException(string message) : base(message) { }
        public NativeCallException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class InvalidTidException : Exception
    {
        public InvalidTidException() { }
        public InvalidTidException(string message) : base(message) { }
        public InvalidTidException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class AppliedMaskMismatchException : Exception
    {
        public AppliedMaskMismatchException() { }
        public AppliedMaskMismatchException(string message) : base(message) { }
        public AppliedMaskMismatchException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class ThreadBootstrapException : Exception
    {
        public ThreadBootstrapException() { }
        public ThreadBootstrapException(string message) : base(message) { }
        public ThreadBootstrapException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class ThreadRuntimeException : Exception
    {
        public ThreadRuntimeException() { }
        public ThreadRuntimeException(string message) : base(message) { }
        public ThreadRuntimeException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class ThreadNotFoundException : Exception
    {
        public ThreadNotFoundException() { }
        public ThreadNotFoundException(string message) : base(message) { }
        public ThreadNotFoundException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class StartAsyncException : Exception
    {
        public StartAsyncException() { }
        public StartAsyncException(string message) : base(message) { }
        public StartAsyncException(string? message, Exception? innerException) : base(message, innerException) { }
    }
    class StartException : Exception
    {
        public StartException() { }
        public StartException(string message) : base(message) { }
        public StartException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class DisposingException : Exception
    {
        public DisposingException() { }
        public DisposingException(string message) : base(message) { }
        public DisposingException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    class TimedOutException : Exception
    {
        public TimedOutException() { }
        public TimedOutException(string message) : base(message) { }
        public TimedOutException(string? message, Exception? innerException) : base(message, innerException) { }
    }
}
