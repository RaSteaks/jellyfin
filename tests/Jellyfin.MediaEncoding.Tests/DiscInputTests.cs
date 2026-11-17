using System;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests;

public class DiscInputTests
{
    [Fact]
    public void AdaptiveDiscBudgets_IncludeLowQualitiesWithoutExceedingTheNegotiatedTotal()
    {
        var video = DiscAdaptiveStreaming.GetVideoBitrates(5_872_000, 128_000);
        Assert.Contains(292_000, video);
        Assert.Contains(1_372_000, video);
        Assert.Equal(5_872_000, video[^1]);
        Assert.All(video, bitrate => Assert.InRange(bitrate + 128_000, 1, 6_000_000));
        Assert.Equal(video.Count, new System.Collections.Generic.HashSet<int>(video).Count);
    }

    [Theory]
    [InlineData(24, 3)]
    [InlineData(23.976023674, 3.003)]
    [InlineData(29.970029831, 3.003)]
    public void AdaptiveDiscGrid_DoesNotAccumulateMillisecondRoundingDrift(double frameRate, double interval)
    {
        Assert.Equal(interval, DiscAdaptiveStreaming.GetSegmentDuration(frameRate, 3), 6);
    }

    [Fact]
    public void DiscStreamMapping_KeepsProbeIndexAfterLibraryFiltering()
    {
        var audio = new MediaStream { Index = 1, InputStreamIndex = 3, Type = MediaStreamType.Audio };
        var sidecar = new MediaStream { Index = 2, IsExternal = true, Path = "/movie.en.srt", Type = MediaStreamType.Subtitle };
        MediaStream[] streams = [new() { Type = MediaStreamType.Video, InputStreamIndex = 0 }, audio, sidecar];

        Assert.Equal(3, EncodingHelper.FindIndex(streams, audio));
        Assert.Equal(0, EncodingHelper.FindIndex(streams, sidecar));
    }

    [Fact]
    public void BluRayInput_UsesSelectedPlaylistAndDiscProtocol()
    {
        var examiner = new Mock<IBlurayExaminer>();
        examiner.Setup(e => e.GetDiscInfo("/media/Film"))
            .Returns(new BlurayDiscInfo { PlaylistName = "00123.MPLS", Files = ["/media/Film/BDMV/STREAM/00001.m2ts"] });
        using var encoder = CreateEncoder(examiner.Object);
        var source = new MediaSourceInfo { Path = "/media/Film", Protocol = MediaProtocol.File, VideoType = VideoType.BluRay, DiscTitle = 123 };

        Assert.Equal("-playlist 123", encoder.GetInputOptions(source));
        Assert.Equal("bluray:\"/media/Film\"", encoder.GetInputPathArgument(source.Path, source));
    }

    [Fact]
    public void DiscInput_WithoutSelection_FailsInsteadOfGuessing()
    {
        var examiner = new Mock<IBlurayExaminer>();
        examiner.Setup(e => e.GetTitles(It.IsAny<string>())).Returns(new[]
        {
            new BlurayDiscInfo { PlaylistName = "00000.MPLS", Chapters = [] },
            new BlurayDiscInfo { PlaylistName = "00001.MPLS", Chapters = [] }
        });
        using var encoder = CreateEncoder(examiner.Object);
        Assert.Throws<InvalidOperationException>(() => encoder.GetInputOptions(new MediaSourceInfo { Path = "/disc.iso", VideoType = VideoType.Iso, IsoType = IsoType.BluRay }));
    }

    [Theory]
    [InlineData(VideoType.Dvd)]
    [InlineData(VideoType.Iso)]
    public void DvdInput_UsesNavigationAndPlainPath(VideoType type)
    {
        using var encoder = CreateEncoder(Mock.Of<IBlurayExaminer>());
        var source = new MediaSourceInfo { Path = "/disc", VideoType = type, IsoType = IsoType.Dvd, DiscTitle = 2 };
        Assert.Equal("-f dvdvideo -title 2", encoder.GetInputOptions(source));
        Assert.Equal("\"/disc\"", encoder.GetInputPathArgument(source.Path, source));
    }

    [Fact]
    public void DvdInput_NoTitleVobs_ReturnsEmptyPlaylist()
    {
        using var encoder = CreateEncoder(Mock.Of<IBlurayExaminer>());
        Assert.Empty(encoder.GetPrimaryPlaylistVobFiles("/empty-disc", null));
    }

    [Fact]
    public void VideoFileInput_DoesNotInspectDisc()
    {
        using var encoder = CreateEncoder(new Mock<IBlurayExaminer>(MockBehavior.Strict).Object);
        var source = new MediaSourceInfo { Path = "/media/Film.mkv", VideoType = VideoType.VideoFile, Protocol = MediaProtocol.File };
        Assert.Empty(encoder.GetInputOptions(source));
        Assert.Equal("file:\"/media/Film.mkv\"", encoder.GetInputPathArgument(source.Path, source));
    }

    private static MediaEncoder CreateEncoder(IBlurayExaminer examiner)
        => new(
            Mock.Of<ILogger<MediaEncoder>>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IFileSystem>(),
            examiner,
            Mock.Of<ILocalizationManager>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IServerConfigurationManager>());
}
