using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Emby.Server.Implementations.Library.Resolvers.Movies;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();
    private static readonly VideoListResolver _videoListResolver = new(_namingOptions);

    [Theory]
    [InlineData("BDMV", VideoType.BluRay)]
    [InlineData("VIDEO_TS", VideoType.Dvd)]
    public void ResolvePath_DiscRoot_PreservesDirectoryAndProviderId(string structure, VideoType type)
    {
        var movie = Assert.IsType<Movie>(ResolveDiscFolders("Film [tmdbid-123]", [structure], structure));
        Assert.Equal(type, movie.VideoType);
        Assert.Equal(Path.Combine("media", "Film [tmdbid-123]"), movie.Path);
        Assert.Equal("123", movie.ProviderIds["Tmdb"]);
    }

    [Theory]
    [InlineData("BDMV")]
    [InlineData("VIDEO_TS")]
    public void ResolvePath_DiscCollection_DoesNotConsumeUnrelatedMovie(string structure)
    {
        Assert.Null(ResolveDiscFolders("Collection", ["Film disc1", "Film disc2", "Other Film"], structure));
        Assert.Null(ResolveDiscFolders("Collection", ["Film disc1", "Film disc2", "Unrelated folder"], structure, "Unrelated folder"));
        Assert.Null(ResolveDiscFolders("Collection", ["Film A", "Film B"], structure));
        Assert.Null(ResolveDiscFolders("Collection", ["Film - Theatrical", "Film - Extended"], structure));
    }

    [Theory]
    [InlineData("BDMV", VideoType.BluRay)]
    [InlineData("VIDEO_TS", VideoType.Dvd)]
    public void ResolvePath_CompleteDiscStack_KeepsExistingParts(string structure, VideoType type)
    {
        var movie = Assert.IsType<Movie>(ResolveDiscFolders("Film", ["Film disc2", "Film disc1"], structure));
        Assert.Equal(type, movie.VideoType);
        Assert.EndsWith("Film disc1", movie.Path, StringComparison.Ordinal);
        Assert.EndsWith("Film disc2", Assert.Single(movie.AdditionalParts), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_NumberedDiscDirectories_UseNumericOrder()
    {
        var movie = Assert.IsType<Movie>(ResolveDiscFolders("Film", ["Disc10", "Disc2", "Disc1"], "BDMV"));
        Assert.EndsWith("Disc1", movie.Path, StringComparison.Ordinal);
        Assert.Equal(new[] { Path.Combine("media", "Film", "Disc2"), Path.Combine("media", "Film", "Disc10") }, movie.AdditionalParts);
    }

    [Fact]
    public void ResolvePath_ExplicitDirectoryEditions_UseExistingAlternateVersions()
    {
        var movie = Assert.IsType<Movie>(ResolveDiscFolders("Film", ["Film - Theatrical", "Film - Extended"], "BDMV"));
        Assert.EndsWith("Film - Extended", movie.Path, StringComparison.Ordinal);
        Assert.EndsWith("Film - Theatrical", Assert.Single(movie.LocalAlternateVersions), StringComparison.Ordinal);
    }

    // Directory entries below are classification fixtures, not playable media.
    private static BaseItem? ResolveDiscFolders(string name, string[] children, string structure, string? emptyFolder = null)
    {
        var root = Path.Combine("media", name);
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        var directory = new Mock<IDirectoryService>();
        directory.Setup(d => d.GetFilePaths(It.IsAny<string>()))
            .Returns<string>(path => [Path.Combine(path, "VTS_01_1.VOB")]);
        directory.Setup(d => d.GetFileSystemEntries(It.IsAny<string>()))
            .Returns<string>(path => Path.GetFileName(path) == emptyFolder ? [] :
                [new FileSystemMetadata { FullName = Path.Combine(path, structure), Name = structure, IsDirectory = true }]);
        var resolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, directory.Object, _videoListResolver);
        return resolver.ResolvePath(new ItemResolveArgs(Mock.Of<IServerApplicationPaths>(), library.Object)
        {
            Parent = new Folder(),
            CollectionType = CollectionType.movies,
            FileInfo = new FileSystemMetadata { FullName = root, IsDirectory = true },
            FileSystemChildren = children.Select(child => new FileSystemMetadata
            {
                FullName = Path.Combine(root, child), Name = child, IsDirectory = true
            }).ToArray()
        });
    }

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }

    [Fact]
    public void ResolveMultiple_GivenTvShowsCollection_CreatesEpisodeItems()
    {
        // For a tvshows collection, the multi-version grouping must still produce
        // Episode BaseItems (not generic Video) so downstream metadata fetching
        // and series-aware logic apply.
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);

        var parent = new Folder { Path = "/TV/Show/Season 1" };
        var files = new List<FileSystemMetadata>
        {
            new() { FullName = "/TV/Show/Season 1/Show - S01E01 - 1080p.mkv", Name = "Show - S01E01 - 1080p.mkv", IsDirectory = false },
            new() { FullName = "/TV/Show/Season 1/Show - S01E01 - 720p.mkv", Name = "Show - S01E01 - 720p.mkv", IsDirectory = false },
            new() { FullName = "/TV/Show/Season 1/Show - S01E02.mkv", Name = "Show - S01E02.mkv", IsDirectory = false }
        };

        var result = movieResolver.ResolveMultiple(parent, files, CollectionType.tvshows, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.IsType<Episode>(item));

        // The S01E01 item should have one alternate version
        var s01e01 = result.Items.Find(i => i.Path.Contains("S01E01", System.StringComparison.Ordinal));
        Assert.NotNull(s01e01);
        Assert.Single(((Video)s01e01).LocalAlternateVersions);
    }

    [Fact]
    public void ResolveMultiple_GivenMoviesCollection_CreatesMovieItems()
    {
        // For a movies collection, the multi-version grouping must produce Movie
        // BaseItems (not generic Video) so downstream movie-specific logic applies.
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);

        var parent = new Folder { Path = "/movies/Inception (2010)" };
        var files = new List<FileSystemMetadata>
        {
            new() { FullName = "/movies/Inception (2010)/Inception (2010) - 1080p.mkv", Name = "Inception (2010) - 1080p.mkv", IsDirectory = false },
            new() { FullName = "/movies/Inception (2010)/Inception (2010) - 720p.mkv", Name = "Inception (2010) - 720p.mkv", IsDirectory = false }
        };

        var result = movieResolver.ResolveMultiple(parent, files, CollectionType.movies, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.All(result.Items, item => Assert.IsType<Movie>(item));
        Assert.Single(((Video)result.Items[0]).LocalAlternateVersions);
    }

    [Fact]
    public void ResolveMultiple_GivenNumberedSampleFiles_IgnoresSamples()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);

        var parent = new Folder { Path = "/movies/La Chimera (2023)" };
        var files = new List<FileSystemMetadata>
        {
            new() { FullName = "/movies/La Chimera (2023)/La Chimera (2023).mkv", Name = "La Chimera (2023).mkv", IsDirectory = false },
            new() { FullName = "/movies/La Chimera (2023)/Sample1.mkv", Name = "Sample1.mkv", IsDirectory = false },
            new() { FullName = "/movies/La Chimera (2023)/Sample2.mkv", Name = "Sample2.mkv", IsDirectory = false }
        };

        var result = movieResolver.ResolveMultiple(parent, files, CollectionType.movies, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        var movie = Assert.Single(result.Items);
        Assert.IsType<Movie>(movie);
        Assert.Equal(files[0].FullName, movie.Path);
    }

    [Fact]
    public void AllExtrasTypesFolderNames_ContainsSampleSingularAndPlural()
    {
        Assert.True(_namingOptions.AllExtrasTypesFolderNames.ContainsKey("sample"));
        Assert.True(_namingOptions.AllExtrasTypesFolderNames.ContainsKey("Sample"));
        Assert.True(_namingOptions.AllExtrasTypesFolderNames.ContainsKey("samples"));
    }

    [Theory]
    [InlineData("Sample")]
    [InlineData("sample")]
    [InlineData("SAMPLE")]
    [InlineData("samples")]
    public void ResolvePath_MovieFolderWithSampleSubfolder_ResolvesToMovie(string sampleDirName)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        libraryManager.Setup(m => m.IgnoreFile(It.IsAny<FileSystemMetadata>(), It.IsAny<BaseItem>())).Returns(false);

        var resolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);
        var args = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            libraryManager.Object)
        {
            Parent = new Folder(),
            CollectionType = CollectionType.movies,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/media/Outer Colony (2026)",
                IsDirectory = true
            },
            FileSystemChildren = new[]
            {
                new FileSystemMetadata
                {
                    FullName = "/media/Outer Colony (2026)/Outer Colony (2026).mkv",
                    Name = "Outer Colony (2026).mkv"
                },
                new FileSystemMetadata
                {
                    FullName = "/media/Outer Colony (2026)/" + sampleDirName,
                    Name = sampleDirName,
                    IsDirectory = true
                }
            }
        };

        Assert.IsType<Movie>(resolver.ResolvePath(args));
    }

    [Fact]
    public void ResolvePath_MovieFolderWithRealSubfolder_DoesNotResolveToSingleMovie()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        libraryManager.Setup(m => m.IgnoreFile(It.IsAny<FileSystemMetadata>(), It.IsAny<BaseItem>())).Returns(false);

        var resolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);
        var args = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            libraryManager.Object)
        {
            Parent = new Folder(),
            CollectionType = CollectionType.movies,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/media/Outer Colony (2026)",
                IsDirectory = true
            },
            FileSystemChildren = new[]
            {
                new FileSystemMetadata
                {
                    FullName = "/media/Outer Colony (2026)/Outer Colony (2026).mkv",
                    Name = "Outer Colony (2026).mkv"
                },
                new FileSystemMetadata
                {
                    FullName = "/media/Outer Colony (2026)/Feature",
                    Name = "Feature",
                    IsDirectory = true
                }
            }
        };

        Assert.Null(resolver.ResolvePath(args));
    }
}
