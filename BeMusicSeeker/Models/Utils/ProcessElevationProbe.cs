using System.Security.Principal;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// Provides the platform-specific process elevation probe used by the shell warning workflow.
/// </summary>
internal static class ProcessElevationProbe
{
    internal static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
