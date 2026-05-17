using System;

namespace Ribbit.Math;

public class FractionException : Exception
{
    public FractionException(string Message, Exception InnerException)
        : base(Message, InnerException)
    {
    }

    public FractionException() : base()
    {
    }

    public FractionException(string message) : base(message)
    {
    }
}
