# Disc playback adaptation

This custom build uses the existing main worktrees and preserves original media.
It does not mount images or create permanent remuxes. Navigation, remux and transcode
remain distinct from Direct Play. Interactive menus, BD-J and decryption are excluded.

## Supported input and naming

| Input | Path |
| --- | --- |
| MKV / MKV Remux | Existing regular-video implementation, without a new media type |
| BDMV directory | BDInfo title catalogue and explicit libbluray playlist input |
| Blu-ray ISO / equivalent IMG | Read-only UDF adapter for BDInfo, original image passed to libbluray |
| VIDEO_TS directory | libdvdnav title enumeration and FFmpeg dvdvideo navigation |
| DVD ISO / equivalent IMG | UDF structure identification, original image passed to dvdvideo |

Extension alone does not establish support. Image classification reads the structure,
not words such as `dvd` in a filename. The tested libdvdread rejects an ISO9660-only
image; that format is not advertised as playable. BIN/CUE, MDF/MDS and NRG are excluded.
Encrypted media remains subject to the underlying libraries' explicit failures.

Single-title directory sidecars use `<directory-name>.<flags>.<ext>` or
`movie.<flags>.<ext>`. ISO/MKV sidecars use the media filename without its extension.
For a multi-title disc, qualify the sidecar with the selected title:

```text
Film/
  movie.nfo
  BDMV/...
  movie.bd-00000.default.en.srt
  movie.bd-00001.en.ass
DVD Film/
  VIDEO_TS/...
  movie.dvd-1.default.en.srt
  movie.dvd-2.forced.zh.srt
  movie.dvd-1.commentary.en.wav
Image Film/
  Film.iso
  Film.dvd-1.default.en.srt
```

Unqualified sidecars are not assigned to every title on a multi-title disc. Keep
sidecar times relative to that title/part. Subtitles for a whole two-disc movie
are not automatically shifted onto the second disc.

Directory editions use the same explicit prefix as file editions:

```text
Feature (2026)/
  Feature (2026) - Remux.mkv
  Feature (2026) - Extended/
    movie.nfo
    Disc1/BDMV/...
    Disc2/BDMV/...
  Feature (2026) - Theatrical/
    movie.nfo
    Disc1/BDMV/...
    Disc2/BDMV/...
```

The normal naming resolver also accepts named parts such as `Feature disc1`.
Numbered parts of the same disc type sort numerically. Mixed DVD/Blu-ray directory
stacks retain the existing independent-item behavior. Unrelated child films remain
separate, and a shared
provider ID never triggers grouping. A directory disc and its internal stream files
are not separate movies. Movie-directory `movie.nfo` takes precedence over the legacy
DVD `VIDEO_TS/VIDEO_TS.nfo` location.

## Title selection and playback

A unique title is selected automatically. Multiple candidates require a user choice;
the Web action sheet shows the title number, duration and chapter count. The player
settings menu can reopen title selection. Cancellation never starts a guessed title.
The per-user remembered title is used on later playback. A removed title requires
selection again, including when only one other title remains.

DVD title enumeration uses the companion `jellyfin-disc-info` executable beside
FFmpeg. Accurate DVD duration/chapters come from preindexing, cached with the media
metadata rather than repeated at every seek. Blu-ray duration/chapters come from MPLS.
The same selected input is used for probing, playback, subtitles and image extraction.
Stream order comes from ffprobe, including compatibility audio streams; BDInfo no
longer replaces that order. Disc tracks also carry the native `InputStreamIndex`,
so library subtitle filtering and appended sidecars cannot shift FFmpeg mapping.
The existing library embedded-subtitle policy still applies. Shared artwork is generated only for unambiguous titles.

The cache key includes image size/mtime or disc structure filenames, sizes and mtimes.
The original media source identity remains stable across an unchanged rescan. Server
input paths use the existing EncoderPath mechanism independently of client path substitution.

## API and storage changes

`MediaSourceInfo.Id` is opaque. Disc titles use `disc-<item-id>-<title-number>`; regular
video IDs are unchanged. Optional source fields are `DiscTitle`, `DiscItemId`,
`DiscVersionId`, `DiscPartIndex`, `DiscPartCount`, `DiscResumePositionTicks`,
`DiscTitleSelected` and `DiscChapters`.

PlaybackInfo accepts `SupportsDiscTitleSelection` and `ForceDiscTitleSelection`.
`RequiresDiscTitleSelection` returns candidates without playable URLs. Clients that
cannot make the required choice receive the existing incompatible-stream response.
Explicit zero StartTimeTicks from the updated Web client retains "play from beginning";
otherwise the selected version can resume at a later part.

AdditionalParts accepts an optional mediaSourceId and returns only the remaining
parts of that version. Web uses actual item/version associations rather than parsing
source IDs as item IDs. All parts remain addressable; resume preference only sorts them.
Disc keyframe/trickplay file caches are not reused as though they described the title.

The additive `AddDiscPlaybackStates` migration creates per-user/item/title progress.
Favorites and ratings stay in ordinary user data; the movie/version's continue-watching
summary reflects the current source. Each title has its own completion state, and a
multi-disc version completes only when its currently selected parts have completed.
An unchanged single title can reuse compatible legacy progress; old progress is not
copied to all new title candidates.

The internal encoder and Blu-ray examiner interfaces gained title enumeration/input
methods. Custom implementations must be rebuilt. HTTP additions are optional, but
only the updated Web flow is the acceptance target for multi-title/multi-disc behavior.

## Verification

Use .NET 10 and `dotnet test Jellyfin.slnx --configuration Release`. Reuse the existing
resolver, track and encoder tests; there is no new test framework or coverage quota.

`tests/fixtures/discs/generate-bluray.sh` authors a 12-second BDMV fixture with two
chapters. Use tsMuxeR 3.1.0 for the validated seek index; a 2.7.0 authoring attempt
produced an invalid seek index and is not passing evidence. `generate-dvd.sh` authors
two navigable DVD titles plus a UDF image, using dvdauthor 0.7.2 and a full FFmpeg build.
Linux authoring needs genisoimage; macOS uses hdiutil. Both scripts require a new output
path and only generate synthetic media.

The existing opt-in `BlurayFixtureTests` uses real filesystem, BDInfo and ffprobe:

```sh
JELLYFIN_TEST_BLURAY=/absolute/fixture/BD-Sample \
JELLYFIN_TEST_FFMPEG=/absolute/custom/ffmpeg \
dotnet test tests/Jellyfin.Server.Implementations.Tests --configuration Release \
  --filter FullyQualifiedName~BlurayFixtureTests
```

The BLURAY variable can also point to the authored ISO. Without these variables the
fixture test is skipped. Playback acceptance separately checks actual HLS bytes,
Web rendering/seeking, title and user isolation, and cross-disc resume.

See the packaging repository's `docker/custom/README.md` and `VERIFICATION.md` for
build inputs, isolated Compose, executed results and limitations. Preserve production
configuration/database backups before applying the schema change. No production data
was used in development.
