using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>Output budgets and the common frame grid for navigated disc HLS.</summary>
public static class DiscAdaptiveStreaming
{
    // The existing Web quality menu's total audio/video budgets, in ascending order.
    private static readonly int[] Bitrates = [420_000, 720_000, 1_500_000, 3_000_000, 4_000_000, 6_000_000, 8_000_000, 10_000_000, 15_000_000, 20_000_000, 40_000_000, 60_000_000, 80_000_000, 120_000_000];

    /// <summary>Returns distinct video budgets without exceeding the negotiated total ceiling.</summary>
    /// <param name="videoLimit">The negotiated maximum video bitrate.</param>
    /// <param name="audioBitrate">The fixed shared audio bitrate.</param>
    /// <returns>Video bitrates from low to high.</returns>
    public static IReadOnlyList<int> GetVideoBitrates(int videoLimit, int audioBitrate)
        => Bitrates.Where(b => b > audioBitrate && b - audioBitrate < videoLimit)
            .Select(b => b - audioBitrate).Append(videoLimit).Distinct().ToArray();

    /// <summary>Gets the segment interval on an integral frame grid.</summary>
    /// <param name="frameRate">The common output frame rate.</param>
    /// <param name="seconds">The requested segment length.</param>
    /// <returns>The actual interval in seconds.</returns>
    public static double GetSegmentDuration(double frameRate, int seconds)
        => frameRate > 0 ? Math.Round(Math.Ceiling(frameRate * seconds) / frameRate, 6) : seconds;

    /// <summary>Gets a conservative segment peak from the encoder's maxrate and VBV budget.</summary>
    /// <param name="videoBitrate">The video maxrate.</param>
    /// <param name="audioBitrate">The shared audio bitrate.</param>
    /// <param name="segmentDuration">The actual frame-aligned segment duration.</param>
    /// <param name="bufferSeconds">The encoder's VBV budget in seconds of video.</param>
    /// <returns>Bits per second including mux overhead.</returns>
    public static int GetPeakBitrate(int videoBitrate, int audioBitrate, double segmentDuration, int bufferSeconds = 2)
        => (int)Math.Min(int.MaxValue, Math.Ceiling((videoBitrate * (1 + (bufferSeconds / segmentDuration)) + audioBitrate) * 1.05));
}
