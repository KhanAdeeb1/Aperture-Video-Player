#!/usr/bin/env bash
# Generates the representative files the libmpv engine is tested against
# (tests/Aperture.Playback.Mpv.Tests, RepresentativeFileTests). Every file is synthetic,
# made from FFmpeg's built-in generators; nothing is downloaded. The expected result for
# each file lives in the test, not here.
# Usage: make-engine-test-media.sh <output-folder>
set -euo pipefail
out="${1:?usage: make-engine-test-media.sh <output-folder>}"
mkdir -p "$out"
cd "$out"

ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
pattern() { echo "testsrc2=s=$1:r=$2:d=$3"; }
tone() { echo "sine=frequency=$1:sample_rate=48000:d=$2"; }
d=8

cat > subs-en.srt <<'SRT'
1
00:00:00,500 --> 00:00:03,500
The harbour lights came on one by one.

2
00:00:04,000 --> 00:00:07,500
Nobody on the pier said a word.
SRT

cat > subs-fr.ass <<'ASS'
[Script Info]
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 720

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,40,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:03.50,Default,,0,0,0,,Les lumières du port s'allumèrent une à une.
Dialogue: 0,0:00:04.00,0:00:07.50,Default,,0,0,0,,Personne sur la jetée ne dit un mot.
ASS

# --- Common formats -------------------------------------------------------------------
ff -f lavfi -i "$(pattern 1280x720 30 $d)" -f lavfi -i "$(tone 440 $d)" -i subs-en.srt \
   -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 128k -c:s mov_text \
   -metadata:s:s:0 language=eng -movflags +faststart h264-aac-timedtext.mp4
ff -f lavfi -i "$(pattern 1920x1080 24 $d)" -f lavfi -i "$(tone 330 $d)" \
   -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le -x265-params log-level=error \
   -c:a ac3 -ac 6 -b:a 384k hevc10-ac3-51.mkv
ff -f lavfi -i "$(pattern 1280x720 30 $d)" -f lavfi -i "$(tone 523 $d)" \
   -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 1M -c:a libopus -b:a 96k vp9-opus.webm
ff -f lavfi -i "$(pattern 1280x720 30 $d)" -f lavfi -i "$(tone 587 $d)" \
   -c:v libsvtav1 -preset 12 -crf 40 -pix_fmt yuv420p -c:a libopus -b:a 96k av1-opus.mkv
# Anamorphic PAL DVD-style: 720x576 stored, 16:9 shown.
ff -f lavfi -i "$(pattern 720x576 25 $d),setdar=16/9" -f lavfi -i "$(tone 392 $d)" \
   -c:v mpeg2video -b:v 4M -c:a mp2 -b:a 192k -f mpegts mpeg2-anamorphic.ts
ff -f lavfi -i "$(pattern 640x480 25 $d)" -f lavfi -i "$(tone 349 $d)" \
   -c:v mpeg4 -q:v 5 -c:a libmp3lame -b:a 128k mpeg4-mp3.avi
ff -f lavfi -i "$(pattern 640x360 25 $d)" -f lavfi -i "$(tone 294 $d)" \
   -c:v wmv2 -b:v 1M -c:a wmav2 -b:a 128k wmv-wma.wmv
ff -f lavfi -i "$(pattern 1280x720 25 $d)" -f lavfi -i "$(tone 262 $d)" \
   -c:v prores_ks -profile:v 2 -c:a pcm_s16le prores-pcm.mov
ff -f lavfi -i "$(pattern 640x360 25 $d)" -f lavfi -i "$(tone 247 $d)" \
   -c:v ffv1 -c:a flac ffv1-flac.mkv
ff -f lavfi -i "$(pattern 640x360 25 $d)" -f lavfi -i "$(tone 220 $d)" \
   -c:v libtheora -q:v 6 -c:a libvorbis -q:a 4 theora-vorbis.ogv
ff -f lavfi -i "$(pattern 540x960 30 $d)" -f lavfi -i "$(tone 466 $d)" \
   -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac portrait.mp4

# --- Tracks: two audio languages, two embedded subtitle tracks, plus external files -----
ff -f lavfi -i "$(pattern 1280x720 24 $d)" -f lavfi -i "$(tone 440 $d)" -f lavfi -i "$(tone 660 $d)" \
   -i subs-en.srt -i subs-fr.ass \
   -map 0 -map 1 -map 2 -map 3 -map 4 \
   -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a:0 aac -c:a:1 ac3 -ac:a:1 6 -c:s:0 srt -c:s:1 ass \
   -metadata:s:a:0 language=eng -metadata:s:a:0 title="Original" \
   -metadata:s:a:1 language=jpn -metadata:s:a:1 title="Commentary" \
   -metadata:s:s:0 language=eng -metadata:s:s:1 language=fre \
   -disposition:a:0 default -disposition:a:1 0 -disposition:s:0 0 -disposition:s:1 0 \
   multi-track.mkv
# Named like its video, so libmpv loads it automatically ("exact" matching).
cp subs-en.srt "h264-aac-timedtext.en.srt"
# Loaded by hand through "Add subtitle file".
cp subs-fr.ass "extra-subtitles.ass"
printf 'This is not a subtitle file.\n\x00\x01\x02' > "broken-subtitles.srt"

# --- Files that should fail, each in its own way ----------------------------------------
head -c 400000 /dev/urandom > damaged.mp4
: > empty.mp4
# moov atom at the end, so the first 40% has no index: unreadable.
ff -f lavfi -i "$(pattern 1280x720 30 $d)" -c:v libx264 -preset ultrafast -pix_fmt yuv420p .moov-at-end.mp4
head -c $(( $(stat -c %s .moov-at-end.mp4) * 4 / 10 )) .moov-at-end.mp4 > truncated-no-index.mp4
rm .moov-at-end.mp4
# A Matroska file cut in half: plays the half that exists, then ends.
ff -f lavfi -i "$(pattern 1280x720 30 16)" -f lavfi -i "$(tone 440 16)" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac .whole.mkv
head -c $(( $(stat -c %s .whole.mkv) / 2 )) .whole.mkv > cut-in-half.mkv
rm .whole.mkv
# Only a subtitle stream: nothing to play.
ff -i subs-en.srt -c:s srt subtitles-only.mkv
# Sound but no picture, under a video extension.
ff -f lavfi -i "$(tone 440 $d)" -c:a aac audio-only.mp4
printf 'Films to watch this winter\n' > not-a-video.txt

rm -f subs-en.srt subs-fr.ass
ls -la
