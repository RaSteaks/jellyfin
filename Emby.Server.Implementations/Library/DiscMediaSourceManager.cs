using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Emby.Server.Implementations.Library;

/// <summary>Disc title sources, sidecars and per-user resume state.</summary>
/// <param name="encoder">The media encoder.</param>
/// <param name="library">The library manager.</param>
/// <param name="subtitles">The existing subtitle resolver.</param>
/// <param name="audio">The existing audio resolver.</param>
/// <param name="directory">The directory service.</param>
/// <param name="database">The database factory.</param>
/// <param name="userData">The existing watched-state rules.</param>
/// <param name="cache">The metadata cache.</param>
public sealed class DiscMediaSourceManager(
    IMediaEncoder encoder,
    ILibraryManager library,
    SubtitleResolver subtitles,
    AudioResolver audio,
    IDirectoryService directory,
    IDbContextFactory<JellyfinDbContext> database,
    IUserDataManager userData,
    IMemoryCache cache) : IDiscMediaSourceManager
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSourceInfo>> Expand(IReadOnlyList<MediaSourceInfo> sources, User? user, CancellationToken cancellationToken)
    {
        var result = new List<MediaSourceInfo>();
        foreach (var physical in sources)
        {
            if (!physical.DiscType.HasValue)
            {
                result.Add(physical);
                continue;
            }

            var root = library.GetItemById<Video>(Guid.Parse(physical.Id))!;
            if (root.IsPlaceHolder)
            {
                result.Add(physical);
                continue;
            }

            var parts = root.GetAdditionalParts(user).Prepend(root).ToArray();
            var ids = parts.Select(p => p.Id).ToArray();
            using var db = database.CreateDbContext();
            var history = user is null ? [] : db.DiscPlaybackStates.AsNoTracking()
                .Where(s => s.UserId == user.Id && ids.Contains(s.ItemId)).ToArray();
            var latestByPart = history.GroupBy(h => h.ItemId).Select(g => g.OrderByDescending(h => h.LastPlayedUtc).First()).ToArray();
            var resumePart = latestByPart.Where(h => h.PositionTicks > 0).OrderByDescending(h => h.LastPlayedUtc).FirstOrDefault()?.ItemId;
            if (!resumePart.HasValue && latestByPart.Any(h => h.Played))
            {
                resumePart = parts.FirstOrDefault(p => !latestByPart.Any(h => h.ItemId == p.Id && h.Played))?.Id;
            }

            var group = new List<MediaSourceInfo>();
            foreach (var item in parts)
            {
                var input = item.Id == root.Id ? physical : item.GetMediaSources(false).First(s => s.Id == item.Id.ToString("N"));
                // Native disc readers always use the server path, even when the
                // displayed source path was substituted for a remote client.
                input.EncoderPath = item.Path;
                input.EncoderProtocol = MediaProtocol.File;
                var version = item.OwnerId != Guid.Empty && library.GetItemById<Video>(item.OwnerId) is { } owner
                    && owner.AdditionalParts.Contains(item.Path, StringComparer.Ordinal) ? owner : item;
                var partIndex = version.Id == item.Id ? 1 : Array.IndexOf(version.AdditionalParts, item.Path) + 2;
                var partCount = version.AdditionalParts.Length + 1;
                var displayName = partCount > 1 ? $"{item.Name} · {partIndex}/{partCount}" : item.Name;
                var titles = await GetTitles(input, cancellationToken).ConfigureAwait(false);
                var states = history.Where(h => h.ItemId == item.Id).ToArray();
                var lastChoice = states.OrderByDescending(h => h.LastPlayedUtc).FirstOrDefault();
                var selected = lastChoice is not null && titles.Any(t => t.Number == lastChoice.Title) ? lastChoice : null;
                var legacyPosition = user is not null && states.Length == 0 && titles.Count == 1 && input.RunTimeTicks == titles[0].RunTimeTicks
                    ? userData.GetUserData(user, item)?.PlaybackPositionTicks ?? 0 : 0;
                foreach (var title in titles)
                {
                    var state = states.FirstOrDefault(h => h.Title == title.Number);
                    group.Add(new MediaSourceInfo
                    {
                        Id = $"disc-{item.Id:N}-{title.Number}",
                        Path = input.Path,
                        Protocol = input.Protocol,
                        EncoderPath = item.Path,
                        EncoderProtocol = MediaProtocol.File,
                        VideoType = input.VideoType,
                        IsoType = input.IsoType,
                        Video3DFormat = input.Video3DFormat,
                        DiscItemId = item.Id,
                        DiscVersionId = version.Id,
                        DiscPartIndex = partIndex,
                        DiscPartCount = partCount,
                        DiscTitle = title.Number,
                        DiscTitleSelected = selected?.Title == title.Number || (titles.Count == 1 && states.Length == 0),
                        DiscResumePositionTicks = state?.PositionTicks ?? legacyPosition,
                        DiscChapters = title.Chapters,
                        RunTimeTicks = title.RunTimeTicks,
                        Name = $"{displayName} · {title.Number} · {TimeSpan.FromTicks(title.RunTimeTicks):hh\\:mm\\:ss}",
                        Container = input.DiscType == MediaBrowser.Model.Entities.IsoType.BluRay ? "mpegts" : "mpeg",
                        SupportsDirectPlay = false,
                        SupportsDirectStream = physical.SupportsDirectStream,
                        SupportsTranscoding = physical.SupportsTranscoding
                    });
                }
            }

            // Keep every part addressable; preference changes ordering, never identity.
            result.AddRange(group.OrderByDescending(s => s.DiscItemId == resumePart));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> Probe(MediaSourceInfo source, CancellationToken cancellationToken)
    {
        if (!source.DiscTitle.HasValue)
        {
            return source;
        }

        var probe = await cache.GetOrCreateAsync($"disc-probe:{CacheKey(source)}:{source.DiscTitle}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
            return await encoder.GetMediaInfo(new MediaInfoRequest
            {
                MediaSource = source,
                MediaType = DlnaProfileType.Video,
                ExtractChapters = true
            }, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var titles = await GetTitles(source, cancellationToken).ConfigureAwait(false);
        var video = library.GetItemById<Video>(source.DiscItemId!.Value)!;
        var key = DiscTitleInfo.GetKey(source.DiscType!.Value, source.DiscTitle.Value);
        var externalAudio = await audio.GetExternalStreamsAsync(video, 0, directory, true, cancellationToken, key, titles.Count == 1).ConfigureAwait(false);
        var externalSubtitles = await subtitles.GetExternalStreamsAsync(video, 0, directory, true, cancellationToken, key, titles.Count == 1).ConfigureAwait(false);
        // Each request owns the stream indices and delivery flags; don't mutate cached probe results.
        var streams = JsonSerializer.Deserialize<List<MediaStream>>(JsonSerializer.SerializeToUtf8Bytes(probe!.MediaStreams))!;
        foreach (var stream in streams)
        {
            stream.InputStreamIndex = stream.Index;
        }

        var embeddedSubtitles = library.GetLibraryOptions(video).AllowEmbeddedSubtitles;
        streams.RemoveAll(s => s.Type == MediaStreamType.Subtitle
            && (embeddedSubtitles == MediaBrowser.Model.Configuration.EmbeddedSubtitleOptions.AllowNone
                || (s.IsTextSubtitleStream && embeddedSubtitles == MediaBrowser.Model.Configuration.EmbeddedSubtitleOptions.AllowImage)
                || (!s.IsTextSubtitleStream && embeddedSubtitles == MediaBrowser.Model.Configuration.EmbeddedSubtitleOptions.AllowText)));
        source.MediaStreams = streams.Concat(externalAudio).Concat(externalSubtitles).ToList();
        for (var i = 0; i < source.MediaStreams.Count; i++)
        {
            source.MediaStreams[i].Index = i;
        }

        source.MediaAttachments = probe.MediaAttachments;
        source.Container = probe.Container;
        source.Timestamp = probe.Timestamp;
        source.Formats = probe.Formats;
        source.Size = probe.Size;
        source.Bitrate = probe.Bitrate;
        if (source.DiscType == MediaBrowser.Model.Entities.IsoType.Dvd)
        {
            source.RunTimeTicks = probe.RunTimeTicks;
            source.DiscChapters = probe.Chapters;
        }

        return source;
    }

    /// <inheritdoc />
    public bool SaveProgress(User user, MediaSourceInfo source, long position)
    {
        var item = library.GetItemById<Video>(source.DiscItemId!.Value)!;
        using var db = database.CreateDbContext();
        var state = db.DiscPlaybackStates.Find(user.Id, item.Id, source.DiscTitle!.Value);
        if (state is null)
        {
            state = new DiscPlaybackState { UserId = user.Id, ItemId = item.Id, Title = source.DiscTitle.Value };
            db.DiscPlaybackStates.Add(state);
        }

        // Completion belongs to this title; never inherit another title's watched flag.
        var titleData = new UserItemData { Key = source.Id, Played = state.Played, PlaybackPositionTicks = state.PositionTicks };
        userData.UpdatePlayState(new Video { RunTimeTicks = source.RunTimeTicks }, titleData, position);
        var data = userData.GetUserData(user, item)!;
        data.PlaybackPositionTicks = titleData.PlaybackPositionTicks;
        data.Played = titleData.Played;
        data.LastPlayedDate = DateTime.UtcNow;
        userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
        state.PositionTicks = titleData.PlaybackPositionTicks;
        state.Played = titleData.Played;
        state.LastPlayedUtc = DateTime.UtcNow;
        db.SaveChanges();

        if (source.DiscVersionId.HasValue && (source.DiscVersionId.Value != item.Id || item.IsStacked))
        {
            var version = library.GetItemById<Video>(source.DiscVersionId.Value)!;
            var summary = userData.GetUserData(user, version)!;
            var parts = version.AdditionalParts.Select(p => library.GetNewItemId(p, typeof(Video))).Append(version.Id).ToArray();
            var completed = db.DiscPlaybackStates.AsNoTracking().Where(s => s.UserId == user.Id && parts.Contains(s.ItemId))
                .ToArray().GroupBy(s => s.ItemId).Select(g => g.OrderByDescending(s => s.LastPlayedUtc).First())
                .Count(s => s.Played) == parts.Length;
            summary.PlaybackPositionTicks = completed ? 0 : state.Played ? source.RunTimeTicks.GetValueOrDefault() : data.PlaybackPositionTicks;
            summary.LastPlayedDate = data.LastPlayedDate;
            summary.Played = completed;
            userData.SaveUserData(user, version, summary, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
        }
        return state.Played;
    }

    private async Task<IReadOnlyList<DiscTitleInfo>> GetTitles(MediaSourceInfo source, CancellationToken cancellationToken)
        => (await cache.GetOrCreateAsync("disc-titles:" + CacheKey(source), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
            var titles = await encoder.GetDiscTitles(source, cancellationToken).ConfigureAwait(false);
            if (source.DiscType == MediaBrowser.Model.Entities.IsoType.Dvd)
            {
                // libdvdnav's quick catalogue duration is approximate. Preindex once
                // here, then share the exact timeline with candidate display and playback.
                foreach (var title in titles)
                {
                    var selected = new MediaSourceInfo
                    {
                        Path = source.EncoderPath ?? source.Path, Protocol = MediaProtocol.File, VideoType = source.VideoType,
                        IsoType = source.IsoType, DiscTitle = title.Number
                    };
                    var info = await encoder.GetMediaInfo(new MediaInfoRequest
                    {
                        MediaSource = selected, MediaType = DlnaProfileType.Video, ExtractChapters = true
                    }, cancellationToken).ConfigureAwait(false);
                    title.RunTimeTicks = info.RunTimeTicks.GetValueOrDefault();
                    title.Chapters = info.Chapters;
                    cache.Set($"disc-probe:{CacheKey(source)}:{title.Number}", info, TimeSpan.FromHours(12));
                }
            }

            return titles;
        }).ConfigureAwait(false))!;

    private static string CacheKey(MediaSourceInfo source)
    {
        var path = source.EncoderPath ?? source.Path;
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }

        var directory = source.DiscType == MediaBrowser.Model.Entities.IsoType.BluRay
            ? Path.Combine(path, "BDMV", "PLAYLIST") : Path.Combine(path, "VIDEO_TS");
        if (!Directory.Exists(directory))
        {
            directory = path;
        }

        return path + ":" + string.Join(';', Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
            .Select(filePath => new FileInfo(filePath)).Select(file => $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
    }
}
