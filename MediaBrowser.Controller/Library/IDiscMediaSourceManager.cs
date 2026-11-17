using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Library;

/// <summary>Resolves explicit disc titles using the existing playback sources.</summary>
public interface IDiscMediaSourceManager
{
    /// <summary>Expands physical disc sources into their navigable titles.</summary>
    /// <param name="sources">The authorized physical sources.</param>
    /// <param name="user">The current user, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Disc titles and unchanged regular sources.</returns>
    Task<IReadOnlyList<MediaSourceInfo>> Expand(IReadOnlyList<MediaSourceInfo> sources, User? user, CancellationToken cancellationToken);

    /// <summary>Probes the selected title and its bound sidecars.</summary>
    /// <param name="source">The selected source.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The probed source.</returns>
    Task<MediaSourceInfo> Probe(MediaSourceInfo source, CancellationToken cancellationToken);

    /// <summary>Saves progress without overwriting another title's position.</summary>
    /// <param name="user">The playback user.</param>
    /// <param name="source">The selected source.</param>
    /// <param name="position">Position within this title.</param>
    /// <returns>Whether this title has completed.</returns>
    bool SaveProgress(User user, MediaSourceInfo source, long position);
}
