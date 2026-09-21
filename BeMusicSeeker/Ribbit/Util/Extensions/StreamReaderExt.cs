using System.IO;

namespace Ribbit.Util.Extensions;

public static class StreamReaderExt
{
    public static string Tail(this StreamReader reader, int posFromEnd, int size)
    {
        if (reader.BaseStream.Length > posFromEnd)
        {
            reader.BaseStream.Seek(-posFromEnd, SeekOrigin.End);
        }
        char[] array = new char[size];
        reader.Read(array, 0, size);
        return new string(array);
    }
}
