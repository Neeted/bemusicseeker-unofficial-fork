using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class TestOptIn
{
    public static void RequireEnvironmentFlag(string name, string purpose)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set " + name + "=1 to run " + purpose + ".");
        }
    }
}
