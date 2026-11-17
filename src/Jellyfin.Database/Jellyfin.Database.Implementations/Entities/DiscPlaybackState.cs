using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>Per-user state for one navigable disc title, independent of movie user-data keys.</summary>
public sealed class DiscPlaybackState
{
    /// <summary>Gets or sets the user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the owning disc item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the MPLS or DVD title number.</summary>
    public int Title { get; set; }

    /// <summary>Gets or sets the resume position.</summary>
    public long PositionTicks { get; set; }

    /// <summary>Gets or sets whether this title is complete.</summary>
    public bool Played { get; set; }

    /// <summary>Gets or sets the last playback time.</summary>
    public DateTime LastPlayedUtc { get; set; }
}
