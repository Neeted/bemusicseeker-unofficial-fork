using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class TestOptIn
{
    public const string ChartInfoFullEnvironmentVariable = "BMS_TEST_CHART_INFO_FULL";
    public const string PerformanceEnvironmentVariable = "BMS_TEST_PERFORMANCE";

    public static void RequireEnvironmentFlag(string name, string purpose)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set " + name + "=1 to run " + purpose + ".");
        }
    }
}
