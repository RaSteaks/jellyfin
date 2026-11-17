#!/usr/bin/env bash
# Author disposable DVD titles from generated video/audio, never from user media.
set -euo pipefail
if [[ $# != 3 ]]; then
    echo "Usage: $0 /absolute/new-output-directory /path/to/ffmpeg /path/to/dvdauthor" >&2
    exit 2
fi
output="$1"
[[ "$output" = /* && ! -e "$output" ]] || { echo "Output must be a new absolute path" >&2; exit 2; }
mkdir -p "$output"
"$2" -nostdin -hide_banner -loglevel error \
    -f lavfi -i testsrc2=size=720x480:rate=30000/1001 \
    -f lavfi -i sine=frequency=440:sample_rate=48000 \
    -t 12 -target ntsc-dvd "$output/source.mpg"
cat > "$output/dvd.xml" <<EOF
<dvdauthor dest="$output/DVD">
<vmgm><menus><video format="ntsc"/></menus></vmgm>
<titleset><titles><video format="ntsc" aspect="4:3"/>
<pgc><vob file="$output/source.mpg" chapters="0,6"/></pgc>
<pgc><vob file="$output/source.mpg" chapters="0,4,8"/></pgc>
</titles></titleset></dvdauthor>
EOF
"$3" -x "$output/dvd.xml" > "$output/author.log" 2>&1
for title in 1 2; do
    cat > "$output/DVD/movie.dvd-$title.default.en.srt" <<EOF
1
00:00:02,000 --> 00:00:04,000
DVD title $title: first cue

2
00:00:07,000 --> 00:00:10,000
DVD title $title: cue after seek
EOF
done
if [[ "$(uname -s)" == Darwin ]]; then
    hdiutil makehybrid -udf -o "$output/DVD.iso" "$output/DVD" > "$output/iso-author.log" 2>&1
else
    genisoimage -udf -dvd-video -o "$output/DVD.iso" "$output/DVD" > "$output/iso-author.log" 2>&1
fi
echo "Authored $output/DVD and DVD.iso; probe and play each separately."
