using System;

namespace Ribbit.Util.Extensions;

public static class DateTimeExt
{
    private static readonly DateTime UNIXEPOCH = new DateTime(1970, 1, 1, 0, 0, 0, 0);

    public static int ToUnixtime(this DateTime time)
    {
        time = time.ToUniversalTime();
        return (int)(time - UNIXEPOCH).TotalSeconds;
    }
}
