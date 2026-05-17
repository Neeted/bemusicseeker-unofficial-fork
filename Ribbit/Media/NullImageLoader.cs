using System;

namespace Ribbit.Media;

public class NullImageLoader : IImageLoader, IDisposable
{
    public bool CanSeek
    {
        get
        {
            throw new NotImplementedException();
        }
    }

    public TimeSpan CurrentTime
    {
        get
        {
            throw new NotImplementedException();
        }
        set
        {
            throw new NotImplementedException();
        }
    }

    public TimeSpan Duration
    {
        get
        {
            throw new NotImplementedException();
        }
    }

    public string FileName
    {
        get
        {
            throw new NotImplementedException();
        }
    }

    public void Suspend()
    {
        throw new NotImplementedException();
    }

    public void Attach()
    {
        throw new NotImplementedException();
    }

    public void Detach()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
