using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.BdInfo;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class BlurayFixtureTests
{
    [Fact]
    public async Task AuthoredDisc_RealBdInfoAndFfprobeAgreeOnSourceAndTracks()
    {
        var path = Environment.GetEnvironmentVariable("JELLYFIN_TEST_BLURAY");
        var ffmpeg = Environment.GetEnvironmentVariable("JELLYFIN_TEST_FFMPEG");
        Assert.SkipWhen(
            string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ffmpeg),
            "Set JELLYFIN_TEST_BLURAY and JELLYFIN_TEST_FFMPEG to the generated 12-second fixture and patched encoder.");
        Assert.NotNull(path);
        Assert.NotNull(ffmpeg);

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.TempDirectory).Returns(Path.GetTempPath());
        var fileSystem = new ManagedFileSystem(NullLogger<ManagedFileSystem>.Instance, paths.Object, []);
        var examiner = new BdInfoExaminer(fileSystem);
        var disc = examiner.GetDiscInfo(path);
        Assert.Equal("00000.MPLS", disc.PlaylistName, ignoreCase: true);
        Assert.InRange(TimeSpan.FromTicks(disc.RunTimeTicks!.Value).TotalSeconds, 11.9, 12.1);
        Assert.Equal(2, disc.Chapters.Length);
        Assert.Equal(6, disc.Chapters[1], precision: 1);
        Assert.Single(disc.Files);

        // Only configuration persistence is replaced; filesystem access, BDInfo,
        // encoder validation and the ffprobe process all use real implementations.
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions());
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ffmpeg"] = ffmpeg }).Build();
        using var encoder = new MediaEncoder(
            NullLogger<MediaEncoder>.Instance,
            config.Object,
            fileSystem,
            examiner,
            Mock.Of<ILocalizationManager>(),
            settings,
            config.Object);
        Assert.True(encoder.SetFFmpegPath());
        var source = new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File, VideoType = File.Exists(path) ? VideoType.Iso : VideoType.BluRay, IsoType = IsoType.BluRay, DiscTitle = 0 };
        Assert.Equal("-playlist 0", encoder.GetInputOptions(source));
        var probe = await encoder.GetMediaInfo(
            new MediaInfoRequest
            {
                MediaSource = source,
                MediaType = DlnaProfileType.Video,
                ExtractChapters = true
            },
            CancellationToken.None);
        Assert.Equal("h264", probe.MediaStreams.First(s => s.Type == MediaStreamType.Video).Codec);
        Assert.Equal("ac3", probe.MediaStreams.First(s => s.Type == MediaStreamType.Audio).Codec);
    }
}
