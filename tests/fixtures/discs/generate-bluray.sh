#!/usr/bin/env bash
# Author a valid, unencrypted disc from synthetic media; never use production data.
set -euo pipefail
if [[ $# != 3 && $# != 4 ]]; then
    echo "Usage: $0 /absolute/new-output-directory /path/to/ffmpeg /path/to/tsMuxeR [duration-seconds]" >&2
    exit 2
fi
output="$1"
encoder="$2"
muxer="$3"
duration="${4:-12}"
[[ "$duration" =~ ^[0-9]+$ && "$duration" -gt 0 ]] || { echo "Duration must be a positive number of seconds" >&2; exit 2; }
[[ "$output" = /* && ! -e "$output" ]] || { echo "Output must be a new absolute path" >&2; exit 2; }
mkdir -p "$output"
"$encoder" -nostdin -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=1920x1080:rate=24 \
    -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t "$duration" -c:v libx264 -preset veryfast -pix_fmt yuv420p -profile:v high -level 4.1 \
    -x264-params bluray-compat=1:nal-hrd=vbr:force-cfr=1:keyint=24:vbv-maxrate=15000:vbv-bufsize=15000 \
    -c:a ac3 -b:a 192k "$output/source.mkv"
cat > "$output/disc.meta" <<EOF
MUXOPT --blu-ray --vbr --custom-chapters=00:00:00.000;00:00:06.000
V_MPEG4/ISO/AVC, "$output/source.mkv", track=1, insertSEI, contSPS
A_AC3, "$output/source.mkv", track=2, lang=eng
EOF
"$muxer" "$output/disc.meta" "$output/BD-Sample" > "$output/author.log" 2>&1
cat > "$output/BD-Sample/movie.default.en.srt" <<'EOF'
1
00:00:02,000 --> 00:00:04,000
Disc A: first cue

2
00:00:07,000 --> 00:00:10,000
Disc A: cue after seek
EOF
if [[ "$duration" -gt 130 ]]; then
    cat >> "$output/BD-Sample/movie.default.en.srt" <<'EOF'

3
00:02:03,000 --> 00:02:08,000
Disc A: cue after the encoding window
EOF
fi
cat > "$output/BD-Sample/movie.nfo" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<movie><title>Synthetic Blu-ray Test</title><year>2026</year><plot>Generated test pattern with two chapters.</plot></movie>
EOF
"$encoder" -version > "$output/encoder-version.txt"
echo "Authored $output/BD-Sample. This proves authoring only; probe and play it separately."
