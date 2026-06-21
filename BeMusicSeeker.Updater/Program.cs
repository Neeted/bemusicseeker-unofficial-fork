using System;

namespace BeMusicSeeker.Updater
{
    internal static class Program
    {
        internal const int ProtocolVersion = 1;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(ProtocolVersion);
                return 0;
            }

            Console.Error.WriteLine("BeMusicSeeker updater is not implemented yet.");
            return 2;
        }
    }
}
