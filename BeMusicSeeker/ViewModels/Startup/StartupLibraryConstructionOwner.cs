using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Creates the concrete library services required by the startup route.
/// </summary>
internal interface IStartupLibraryFactory
{
    /// <summary>
    /// Creates the library for the supplied immutable startup profile.
    /// </summary>
    /// <param name="libraryProfile">The profile captured for this startup attempt.</param>
    /// <returns>The constructed library.</returns>
    BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile);

    /// <summary>
    /// Creates the playlist using the library created for the same startup profile.
    /// </summary>
    /// <param name="libraryProfile">The profile captured for this startup attempt.</param>
    /// <param name="library">The library created from <paramref name="libraryProfile"/>.</param>
    /// <returns>The constructed playlist.</returns>
    BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library);
}

/// <summary>
/// Applies the two startup library construction stages to the shell-owned consumers.
/// </summary>
internal interface IStartupLibraryApplicationPort
{
    /// <summary>
    /// Attaches the library before playlist construction begins.
    /// </summary>
    /// <param name="library">The exact library created for the startup profile.</param>
    void AttachStartupLibrary(BMSLibrary library);

    /// <summary>
    /// Applies the completed library and playlist services to the shell consumers.
    /// </summary>
    /// <param name="services">The exact services returned by the construction step.</param>
    void AttachStartupServices(StartupLibraryServices services);
}

/// <summary>
/// Owns the ordered construction and application of startup library services.
/// </summary>
internal sealed class StartupLibraryConstructionOwner
{
    private readonly IStartupLibraryFactory factory;

    /// <summary>
    /// Initializes a construction owner with its explicit service factory.
    /// </summary>
    /// <param name="factory">The factory used for both startup construction stages.</param>
    internal StartupLibraryConstructionOwner(IStartupLibraryFactory factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Creates and applies the ordered startup library services.
    /// </summary>
    /// <param name="libraryProfile">The immutable profile captured for startup.</param>
    /// <param name="applicationPort">The narrow shell application boundary.</param>
    /// <returns>The exact services applied to the shell consumers.</returns>
    internal StartupLibraryServices CreateAndApply(
        LibraryProfile libraryProfile,
        IStartupLibraryApplicationPort applicationPort)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        if (applicationPort == null)
        {
            throw new ArgumentNullException(nameof(applicationPort));
        }

        BMSLibrary library = factory.CreateBmsLibrary(libraryProfile)
            ?? throw new InvalidOperationException("Startup library factory returned null.");
        StartupLibraryConstruction construction = new(libraryProfile, library);
        applicationPort.AttachStartupLibrary(construction.Library);

        BMSPlaylist playlist = factory.CreateBmsPlaylist(construction.Profile, construction.Library)
            ?? throw new InvalidOperationException("Startup playlist factory returned null.");
        StartupLibraryServices services = new(construction.Profile, construction.Library, playlist);
        if (!construction.Profile.OperationModeLR2DB)
        {
            construction.Library.SearchTargets.AddRange(construction.Profile.SearchRoots);
        }
        applicationPort.AttachStartupServices(services);
        return services;
    }
}

/// <summary>
/// Immutable result of the first startup library construction stage.
/// </summary>
internal sealed class StartupLibraryConstruction
{
    /// <summary>
    /// Initializes a library construction stage.
    /// </summary>
    /// <param name="profile">The profile used to construct the library.</param>
    /// <param name="library">The constructed library.</param>
    internal StartupLibraryConstruction(LibraryProfile profile, BMSLibrary library)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>
    /// Gets the exact profile passed to the factory.
    /// </summary>
    internal LibraryProfile Profile { get; }

    /// <summary>
    /// Gets the library created from <see cref="Profile"/>.
    /// </summary>
    internal BMSLibrary Library { get; }
}

/// <summary>
/// Immutable startup service bundle produced after both construction stages succeed.
/// </summary>
internal sealed class StartupLibraryServices
{
    /// <summary>
    /// Initializes a completed startup service bundle.
    /// </summary>
    /// <param name="profile">The profile used for both service constructions.</param>
    /// <param name="library">The constructed library.</param>
    /// <param name="playlist">The constructed playlist.</param>
    internal StartupLibraryServices(LibraryProfile profile, BMSLibrary library, BMSPlaylist playlist)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
    }

    /// <summary>
    /// Gets the exact profile used for both stages.
    /// </summary>
    internal LibraryProfile Profile { get; }

    /// <summary>
    /// Gets the library created before the playlist.
    /// </summary>
    internal BMSLibrary Library { get; }

    /// <summary>
    /// Gets the playlist created from <see cref="Library"/>.
    /// </summary>
    internal BMSPlaylist Playlist { get; }
}
