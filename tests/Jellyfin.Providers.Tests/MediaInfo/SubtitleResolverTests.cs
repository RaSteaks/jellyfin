using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class SubtitleResolverTests
{
    private readonly SubtitleResolver _subtitleResolver;

    public SubtitleResolverTests()
    {
        // prep BaseItem and Video for calls made that expect managers
        Video.RecordingsManager = Mock.Of<IRecordingsManager>();

        var applicationPaths = new Mock<IServerApplicationPaths>().Object;
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.ApplicationPaths)
            .Returns(applicationPaths);
        BaseItem.ConfigurationManager = serverConfig.Object;

        // build resolver to test with
        var localizationManager = Mock.Of<ILocalizationManager>();

        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        mediaEncoder.Setup(me => me.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Returns<MediaInfoRequest, CancellationToken>((_, _) => Task.FromResult(new MediaBrowser.Model.MediaInfo.MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new()
                    {
                        Type = MediaStreamType.Subtitle
                    }
                }
            }));

        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.VideoDirectoryRegex)))
            .Returns(true);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.MetadataDirectoryRegex)))
            .Returns(true);

        _subtitleResolver = new SubtitleResolver(Mock.Of<ILogger<SubtitleResolver>>(), localizationManager, mediaEncoder.Object, fileSystem.Object, new NamingOptions());
    }

    [Theory]
    [InlineData(VideoType.BluRay, false)]
    [InlineData(VideoType.BluRay, true)]
    [InlineData(VideoType.Dvd, false)]
    [InlineData(VideoType.Dvd, true)]
    public async Task GetExternalStreams_DiscSidecars_StayWithSelectedDisc(VideoType type, bool stacked)
    {
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();
        var discPath = Path.Combine(MediaInfoResolverTests.VideoDirectoryPath, "Film.2026 disc1");
        var subtitlePath = Path.Combine(discPath, "movie.default.forced.srt");
        var namedSubtitlePath = Path.Combine(discPath, "Film.2026 disc1.srt");
        var video = new Movie
        {
            Path = discPath,
            VideoType = type,
            AdditionalParts = stacked ? [Path.Combine(MediaInfoResolverTests.VideoDirectoryPath, "Film.2026 disc2")] : []
        };
        var directory = new Mock<IDirectoryService>(MockBehavior.Strict);
        directory.Setup(d => d.GetFilePaths(discPath, false))
            .Returns([subtitlePath, namedSubtitlePath, Path.Combine(discPath, "movie2.srt"), Path.Combine(discPath, "Other Film.srt")]);
        directory.Setup(d => d.GetFilePaths(video.GetInternalMetadataPath(), false)).Returns([]);

        var streams = await _subtitleResolver.GetExternalStreamsAsync(video, 4, directory.Object, false, CancellationToken.None);

        Assert.Equal(2, streams.Count);
        var generic = Assert.Single(streams, stream => stream.Path == subtitlePath);
        Assert.True(generic.IsExternal);
        Assert.True(generic.IsDefault);
        Assert.True(generic.IsForced);
        Assert.Equal(new[] { 4, 5 }, streams.Select(stream => stream.Index));
        Assert.Contains(streams, stream => stream.Path == namedSubtitlePath);
        directory.Verify(d => d.GetFilePaths(MediaInfoResolverTests.VideoDirectoryPath, It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData("My.Video.srt", false, true)]
    [InlineData("My.Video.mp3", false, false)]
    [InlineData("My.Video.srt", true, true)]
    [InlineData("My.Video.mp3", true, false)]
    public async Task GetExternalStreams_MixedFilenames_PicksSubtitles(string file, bool metadataDirectory, bool matches)
    {
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();

        var video = new Movie
        {
            Path = MediaInfoResolverTests.VideoDirectoryPath + "/My.Video.mkv"
        };

        var directoryService = MediaInfoResolverTests.GetDirectoryServiceForExternalFile(file, metadataDirectory);
        var streams = await _subtitleResolver.GetExternalStreamsAsync(video, 0, directoryService, false, CancellationToken.None);

        if (matches)
        {
            Assert.Single(streams);
            var actual = streams[0];
            Assert.Equal(MediaStreamType.Subtitle, actual.Type);
        }
        else
        {
            Assert.Empty(streams);
        }
    }
}
