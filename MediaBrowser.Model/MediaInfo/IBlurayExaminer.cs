using System.Collections.Generic;

namespace MediaBrowser.Model.MediaInfo;

/// <summary>
/// Interface IBlurayExaminer.
/// </summary>
public interface IBlurayExaminer
{
    /// <summary>
    /// Gets the disc info.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>BlurayDiscInfo.</returns>
    BlurayDiscInfo GetDiscInfo(string path);

    /// <summary>Gets the distinct playable titles from a directory or UDF image.</summary>
    /// <param name="path">The directory or image path.</param>
    /// <returns>The titles in stable playlist order.</returns>
    IReadOnlyList<BlurayDiscInfo> GetTitles(string path);
}
