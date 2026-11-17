using System.Globalization;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>A navigable title from a directory or image, without a guessed stream file.</summary>
public sealed class DiscTitleInfo
{
    /// <summary>Gets or sets the MPLS number or DVD title number.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets the duration in ticks.</summary>
    public long RunTimeTicks { get; set; }

    /// <summary>Gets or sets the number of chapters before a full title probe.</summary>
    public int ChapterCount { get; set; }

    /// <summary>Gets or sets title chapters.</summary>
    public ChapterInfo[] Chapters { get; set; } = [];

    /// <summary>Gets the filename suffix binding a sidecar to one title.</summary>
    /// <param name="type">The disc type.</param>
    /// <param name="number">The MPLS/DVD title number.</param>
    /// <returns>The stable sidecar suffix.</returns>
    public static string GetKey(IsoType type, int number)
        => type == IsoType.BluRay ? "bd-" + number.ToString("D5", CultureInfo.InvariantCulture) : "dvd-" + number.ToString(CultureInfo.InvariantCulture);
}
