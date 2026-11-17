#pragma warning disable CA1068, CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.MediaInfo
{
    public class FFProbeVideoInfo
    {
        private readonly ILogger<FFProbeVideoInfo> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IBlurayExaminer _blurayExaminer;
        private readonly ILocalizationManager _localization;
        private readonly IChapterManager _chapterManager;
        private readonly IServerConfigurationManager _config;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ILibraryManager _libraryManager;
        private readonly AudioResolver _audioResolver;
        private readonly SubtitleResolver _subtitleResolver;
        private readonly IMediaAttachmentRepository _mediaAttachmentRepository;
        private readonly IMediaStreamRepository _mediaStreamRepository;

        public FFProbeVideoInfo(
            ILogger<FFProbeVideoInfo> logger,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            IBlurayExaminer blurayExaminer,
            ILocalizationManager localization,
            IChapterManager chapterManager,
            IServerConfigurationManager config,
            ISubtitleManager subtitleManager,
            ILibraryManager libraryManager,
            AudioResolver audioResolver,
            SubtitleResolver subtitleResolver,
            IMediaAttachmentRepository mediaAttachmentRepository,
            IMediaStreamRepository mediaStreamRepository)
        {
            _logger = logger;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _blurayExaminer = blurayExaminer;
            _localization = localization;
            _chapterManager = chapterManager;
            _config = config;
            _subtitleManager = subtitleManager;
            _libraryManager = libraryManager;
            _audioResolver = audioResolver;
            _subtitleResolver = subtitleResolver;
            _mediaAttachmentRepository = mediaAttachmentRepository;
            _mediaStreamRepository = mediaStreamRepository;
        }

        public async Task<ItemUpdateType> ProbeVideo<T>(
            T item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
            where T : Video
        {
            BlurayDiscInfo? blurayDiscInfo = null;
            string? titleKey = null;
            var includeUnqualified = true;

            Model.MediaInfo.MediaInfo? mediaInfoResult = null;

            if (!item.IsShortcut || options.EnableRemoteContentProbe)
            {
                var source = new MediaSourceInfo { Path = item.Path, VideoType = item.VideoType, IsoType = item.IsoType };
                if (source.DiscType.HasValue)
                {
                    var titles = await _mediaEncoder.GetDiscTitles(source, cancellationToken).ConfigureAwait(false);
                    if (titles.Count == 0)
                    {
                        throw new InvalidOperationException($"No navigable titles found in {item.Path}.");
                    }

                    // A representative title supplies library metadata, not a playback default.
                    // Playback resolves the user's explicit title before generating any URL.
                    var title = titles.OrderByDescending(t => t.RunTimeTicks).ThenBy(t => t.Number).First();
                    titleKey = DiscTitleInfo.GetKey(source.DiscType.Value, title.Number);
                    includeUnqualified = titles.Count == 1;
                    mediaInfoResult = await GetMediaInfo(item, cancellationToken, title).ConfigureAwait(false);
                    if (source.DiscType == IsoType.BluRay)
                    {
                        blurayDiscInfo = new BlurayDiscInfo
                        {
                            RunTimeTicks = title.RunTimeTicks,
                            Chapters = title.Chapters.Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds).ToArray()
                        };
                    }
                }
                else
                {
                    mediaInfoResult = await GetMediaInfo(item, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            await Fetch(item, cancellationToken, mediaInfoResult, blurayDiscInfo, options, titleKey, includeUnqualified).ConfigureAwait(false);

            return ItemUpdateType.MetadataImport;
        }

        private Task<Model.MediaInfo.MediaInfo> GetMediaInfo(
            Video item,
            CancellationToken cancellationToken,
            DiscTitleInfo? title = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = item.Path;
            var protocol = item.PathProtocol ?? MediaProtocol.File;

            if (item.IsShortcut)
            {
                path = item.ShortcutPath;
                protocol = _mediaSourceManager.GetPathProtocol(path);
            }

            return _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    ExtractChapters = true,
                    MediaType = DlnaProfileType.Video,
                    MediaSource = new MediaSourceInfo
                    {
                        Path = path,
                        Protocol = protocol,
                        VideoType = item.VideoType,
                        IsoType = item.IsoType,
                        DiscTitle = title?.Number
                    }
                },
                cancellationToken);
        }

        protected async Task Fetch(
            Video video,
            CancellationToken cancellationToken,
            Model.MediaInfo.MediaInfo? mediaInfo,
            BlurayDiscInfo? blurayInfo,
            MetadataRefreshOptions options,
            string? titleKey = null,
            bool includeUnqualified = true)
        {
            List<MediaStream> mediaStreams = new List<MediaStream>();
            IReadOnlyList<MediaAttachment> mediaAttachments;
            ChapterInfo[] chapters;

            await AddExternalAudioAsync(video, mediaStreams, options, cancellationToken, titleKey, includeUnqualified).ConfigureAwait(false);

            if (mediaInfo is not null)
            {
                mediaStreams.AddRange(mediaInfo.MediaStreams);

                mediaAttachments = mediaInfo.MediaAttachments;
                video.TotalBitrate = mediaInfo.Bitrate;
                video.RunTimeTicks = mediaInfo.RunTimeTicks;
                video.Container = mediaInfo.Container;
                var videoType = video.VideoType;
                if (videoType == VideoType.BluRay || videoType == VideoType.Dvd)
                {
                    video.Size = mediaInfo.Size;
                }

                chapters = mediaInfo.Chapters ?? [];
                if (blurayInfo is not null)
                {
                    FetchBdInfo(video, ref chapters, blurayInfo);
                }
            }
            else
            {
                foreach (var mediaStream in video.GetMediaStreams())
                {
                    if (!mediaStream.IsExternal)
                    {
                        mediaStreams.Add(mediaStream);
                    }
                }

                mediaAttachments = [];
                chapters = [];
            }

            // Download and insert external streams before the streams from the file to preserve stream IDs on remote videos
            await AddExternalSubtitlesAsync(video, mediaStreams, options, cancellationToken, titleKey, includeUnqualified).ConfigureAwait(false);

            for (var i = 0; i < mediaStreams.Count; i++)
            {
                mediaStreams[i].Index = i;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(video);

            if (mediaInfo is not null)
            {
                FetchEmbeddedInfo(video, mediaInfo, options, libraryOptions);
                FetchPeople(video, mediaInfo, options);
                video.Timestamp = mediaInfo.Timestamp;
                video.Video3DFormat ??= mediaInfo.Video3DFormat;
            }

            if (libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowText || libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowNone)
            {
                _logger.LogDebug("Disabling embedded image subtitles for {Path} due to DisableEmbeddedImageSubtitles setting", video.Path);
                mediaStreams.RemoveAll(i => i.Type == MediaStreamType.Subtitle && !i.IsExternal && !i.IsTextSubtitleStream);
            }

            if (libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowImage || libraryOptions.AllowEmbeddedSubtitles == EmbeddedSubtitleOptions.AllowNone)
            {
                _logger.LogDebug("Disabling embedded text subtitles for {Path} due to DisableEmbeddedTextSubtitles setting", video.Path);
                mediaStreams.RemoveAll(i => i.Type == MediaStreamType.Subtitle && !i.IsExternal && i.IsTextSubtitleStream);
            }

            var videoStream = mediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Video);

            video.Height = videoStream?.Height ?? 0;
            video.Width = videoStream?.Width ?? 0;

            video.DefaultVideoStreamIndex = videoStream?.Index;

            video.HasSubtitles = mediaStreams.Any(i => i.Type == MediaStreamType.Subtitle);

            _mediaStreamRepository.SaveMediaStreams(video.Id, mediaStreams, cancellationToken);

            _mediaAttachmentRepository.SaveMediaAttachments(video.Id, mediaAttachments, cancellationToken);

            if (options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh
                || options.MetadataRefreshMode == MetadataRefreshMode.Default)
            {
                if (_config.Configuration.DummyChapterDuration > 0 && chapters.Length <= 1 && mediaStreams.Any(i => i.Type == MediaStreamType.Video))
                {
                    chapters = CreateDummyChapters(video);
                }

                NormalizeChapterNames(chapters);

                var extractDuringScan = false;
                if (libraryOptions is not null)
                {
                    extractDuringScan = libraryOptions.ExtractChapterImagesDuringLibraryScan;
                }

                await _chapterManager.RefreshChapterImages(video, options.DirectoryService, chapters, extractDuringScan, false, cancellationToken).ConfigureAwait(false);

                _chapterManager.SaveChapters(video, chapters);
            }
        }

        private void NormalizeChapterNames(ChapterInfo[] chapters)
        {
            for (int i = 0; i < chapters.Length; i++)
            {
                string? name = chapters[i].Name;
                // Check if the name is empty and/or if the name is a time
                // Some ripping programs do that.
                if (string.IsNullOrWhiteSpace(name)
                    || TimeSpan.TryParse(name, out _))
                {
                    chapters[i].Name = string.Format(
                        CultureInfo.InvariantCulture,
                        _localization.GetLocalizedString("ChapterNameValue"),
                        (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static void FetchBdInfo(Video video, ref ChapterInfo[] chapters, BlurayDiscInfo blurayInfo)
        {
            // Keep ffprobe's actual stream ordering (including TrueHD compatibility
            // streams); MPLS supplies only the title timeline and chapters.
            video.RunTimeTicks = blurayInfo.RunTimeTicks;
            chapters = blurayInfo.Chapters.Select(c => new ChapterInfo
            {
                StartPositionTicks = TimeSpan.FromSeconds(c).Ticks
            }).ToArray();
        }

        /// <summary>
        /// Gets information about the longest playlist on a bdrom.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>VideoStream.</returns>
        private BlurayDiscInfo? GetBDInfo(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            try
            {
                return _blurayExaminer.GetDiscInfo(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting BDInfo");
                return null;
            }
        }

        internal void FetchEmbeddedInfo(Video video, Model.MediaInfo.MediaInfo data, MetadataRefreshOptions refreshOptions, LibraryOptions libraryOptions)
        {
            var replaceData = refreshOptions.ReplaceAllMetadata;

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.OfficialRating))
            {
                if (string.IsNullOrWhiteSpace(video.OfficialRating) || replaceData)
                {
                    video.OfficialRating = data.OfficialRating;
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Genres))
            {
                if (video.Genres.Length == 0 || replaceData)
                {
                    video.Genres = [];

                    foreach (var genre in data.Genres.Trimmed())
                    {
                        video.AddGenre(genre);
                    }
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Studios))
            {
                if (video.Studios.Length == 0 || replaceData)
                {
                    video.SetStudios(data.Studios);
                }
            }

            if (!video.IsLocked && video is MusicVideo musicVideo)
            {
                if (string.IsNullOrEmpty(musicVideo.Album) || replaceData)
                {
                    musicVideo.Album = data.Album;
                }

                if (musicVideo.Artists.Count == 0 || replaceData)
                {
                    musicVideo.Artists = data.Artists;
                }
            }

            // Extras have no release date of their own, they inherit it from the item they belong to.
            var useContainerDates = video.ExtraType is null;
            if (useContainerDates && data.ProductionYear is not null)
            {
                if (video.ProductionYear is null || replaceData)
                {
                    video.ProductionYear = data.ProductionYear;
                }
            }

            if (useContainerDates)
            {
                // creation_time is the mux time, so it only stands in where the tags gave no date
                // and it cannot contradict a year we already know, which in practice is home video.
                var containerDate = data.PremiereDate;
                if (containerDate is null
                    && (video.ProductionYear is null || data.ContainerCreationDate?.ToLocalTime().Year == video.ProductionYear))
                {
                    containerDate = data.ContainerCreationDate;
                }

                if (containerDate is not null && (video.PremiereDate is null || replaceData))
                {
                    video.PremiereDate = containerDate;
                }
            }

            if (data.IndexNumber.HasValue)
            {
                if (!video.IndexNumber.HasValue || replaceData)
                {
                    video.IndexNumber = data.IndexNumber;
                }
            }

            if (data.ParentIndexNumber.HasValue)
            {
                if (!video.ParentIndexNumber.HasValue || replaceData)
                {
                    video.ParentIndexNumber = data.ParentIndexNumber;
                }
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Name))
            {
                if (!string.IsNullOrWhiteSpace(data.Name) && libraryOptions.EnableEmbeddedTitles)
                {
                    // Separate option to use the embedded name for extras because it will often be the same name as the movie
                    if (!video.ExtraType.HasValue || libraryOptions.EnableEmbeddedExtrasTitles)
                    {
                        video.Name = data.Name;
                    }
                }

                if (!string.IsNullOrWhiteSpace(data.ForcedSortName))
                {
                    video.ForcedSortName = data.ForcedSortName;
                }
            }

            // If we don't have a ProductionYear try and get it from PremiereDate
            if (useContainerDates && video.PremiereDate is not null && video.ProductionYear is null)
            {
                video.ProductionYear = video.PremiereDate.Value.ToLocalTime().Year;
            }

            if (!video.IsLocked && !video.LockedFields.Contains(MetadataField.Overview))
            {
                if (string.IsNullOrWhiteSpace(video.Overview) || replaceData)
                {
                    video.Overview = data.Overview;
                }
            }
        }

        private void FetchPeople(Video video, Model.MediaInfo.MediaInfo data, MetadataRefreshOptions options)
        {
            if (video.IsLocked
                || video.LockedFields.Contains(MetadataField.Cast)
                || data.People.Length == 0)
            {
                return;
            }

            if (options.ReplaceAllMetadata || _libraryManager.GetPeople(video).Count == 0)
            {
                var people = new List<PersonInfo>();

                foreach (var person in data.People)
                {
                    if (!string.IsNullOrWhiteSpace(person.Name))
                    {
                        PeopleHelper.AddPerson(people, new PersonInfo
                        {
                            Name = person.Name,
                            Type = person.Type,
                            Role = person.Role?.Trim()
                        });
                    }
                }

                _libraryManager.UpdatePeople(video, people);
            }
        }

        /// <summary>
        /// Adds the external subtitles.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <param name="currentStreams">The current streams.</param>
        /// <param name="options">The refreshOptions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="titleKey">The selected disc title suffix, if any.</param>
        /// <param name="includeUnqualified">Whether unqualified sidecars belong to this title.</param>
        /// <returns>Task.</returns>
        private async Task AddExternalSubtitlesAsync(
            Video video,
            List<MediaStream> currentStreams,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken,
            string? titleKey,
            bool includeUnqualified)
        {
            var externalSubtitleStreams = await _subtitleResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, false, cancellationToken, titleKey, includeUnqualified).ConfigureAwait(false);

            var enableSubtitleDownloading = options.MetadataRefreshMode == MetadataRefreshMode.Default ||
                                            options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh;

            var libraryOptions = _libraryManager.GetLibraryOptions(video);

            if (includeUnqualified && enableSubtitleDownloading && libraryOptions.SubtitleDownloadLanguages is not null)
            {
                var downloadedLanguages = await new SubtitleDownloader(
                    _logger,
                    _subtitleManager).DownloadSubtitles(
                        video,
                        currentStreams.Concat(externalSubtitleStreams).ToList(),
                        libraryOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                        libraryOptions.SkipSubtitlesIfAudioTrackMatches,
                        libraryOptions.RequirePerfectSubtitleMatch,
                        libraryOptions.SubtitleDownloadLanguages,
                        libraryOptions.DisabledSubtitleFetchers,
                        libraryOptions.SubtitleFetcherOrder,
                        true,
                        cancellationToken).ConfigureAwait(false);

                // Rescan
                if (downloadedLanguages.Count > 0)
                {
                    externalSubtitleStreams = await _subtitleResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, true, cancellationToken, titleKey, includeUnqualified).ConfigureAwait(false);
                }
            }

            // Keep the full inventory for change detection, but publish only this title's streams.
            video.SubtitleFiles = titleKey is null
                ? externalSubtitleStreams.Select(i => i.Path).Distinct().ToArray()
                : _subtitleResolver.GetExternalFiles(video, options.DirectoryService, false).Select(i => i.Path).Distinct().ToArray();

            currentStreams.InsertRange(0, externalSubtitleStreams);
        }

        /// <summary>
        /// Adds the external audio.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <param name="currentStreams">The current streams.</param>
        /// <param name="options">The refreshOptions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="titleKey">The selected disc title suffix, if any.</param>
        /// <param name="includeUnqualified">Whether unqualified sidecars belong to this title.</param>
        private async Task AddExternalAudioAsync(
            Video video,
            List<MediaStream> currentStreams,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken,
            string? titleKey,
            bool includeUnqualified)
        {
            var externalAudioStreams = await _audioResolver.GetExternalStreamsAsync(video, 0, options.DirectoryService, false, cancellationToken, titleKey, includeUnqualified).ConfigureAwait(false);

            video.AudioFiles = titleKey is null
                ? externalAudioStreams.Select(i => i.Path).Distinct().ToArray()
                : _audioResolver.GetExternalFiles(video, options.DirectoryService, false).Select(i => i.Path).Distinct().ToArray();

            currentStreams.AddRange(externalAudioStreams);
        }

        /// <summary>
        /// Creates dummy chapters.
        /// </summary>
        /// <param name="video">The video.</param>
        /// <returns>An array of dummy chapters.</returns>
        internal ChapterInfo[] CreateDummyChapters(Video video)
        {
            var runtime = video.RunTimeTicks.GetValueOrDefault();

            // Only process files with a runtime greater than 0 and less than 12h. The latter are likely corrupted.
            if (runtime < 0 || runtime > TimeSpan.FromHours(12).Ticks)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} has an invalid runtime of {1} minutes",
                        video.Name,
                        TimeSpan.FromTicks(runtime).TotalMinutes));
            }

            long dummyChapterDuration = TimeSpan.FromSeconds(_config.Configuration.DummyChapterDuration).Ticks;

            if (runtime <= 0)
            {
                return [];
            }

            int chapterCount = Math.Max(1, (int)(runtime / dummyChapterDuration));
            var chapters = new ChapterInfo[chapterCount];

            long currentChapterTicks = 0;
            for (int i = 0; i < chapterCount; i++)
            {
                chapters[i] = new ChapterInfo
                {
                    StartPositionTicks = currentChapterTicks
                };

                currentChapterTicks += dummyChapterDuration;
            }

            return chapters;
        }
    }
}
