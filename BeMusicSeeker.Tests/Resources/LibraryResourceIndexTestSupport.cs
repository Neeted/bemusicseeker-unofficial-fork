using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>Observes the resource owner while exercising the real library command ingress.</summary>
internal static class LibraryResourceIndexTestSupport
{
    /// <summary>
    /// Gets the existing owner only for initial scan setup and snapshot observation. Mutation
    /// under test still goes through the library command; no private workflow is invoked.
    /// Retire this reflection helper if the library gains a suitable snapshot diagnostic surface.
    /// </summary>
    internal static LibraryResourceIndexOwner GetOwner(BMSLibrary library)
    {
        FieldInfo field = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (LibraryResourceIndexOwner)field.GetValue(library)!;
    }
}
