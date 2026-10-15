#!/usr/bin/env bash
# Generates the synthetic test videos used for visual review in CI.
# Every file is made here from FFmpeg's built-in generators (no third-party footage).
# The titles are invented so screens can be judged with realistic-looking names.
# Usage: make-test-media.sh <output-folder>
set -euo pipefail
out="${1:?usage: make-test-media.sh <output-folder>}"
mkdir -p "$out"

size=960x540
rate=12

clip() { # name minutes c0 c1 c2 tone-hz
  local name="$1" minutes="$2" c0="$3" c1="$4" c2="$5" tone="$6"
  local segment="$out/.segment.mp4"
  # Render 30 s once, then repeat it without re-encoding to reach the full length.
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "gradients=s=${size}:r=${rate}:c0=${c0}:c1=${c1}:c2=${c2}:n=3:x0=480:y0=0:x1=480:y1=540:type=linear:speed=0.004,format=yuv420p" \
    -f lavfi -i "sine=frequency=${tone}:sample_rate=48000" \
    -t 30 -af volume=0.03 \
    -c:v libx264 -preset ultrafast -crf 27 -g 120 -c:a aac -b:a 48k \
    "$segment"
  ffmpeg -hide_banner -loglevel error -y -stream_loop -1 -i "$segment" -t "$((minutes * 60))" -c copy -movflags +faststart "$out/$name.mp4"
  rm -f "$segment"
  echo "made $name.mp4 (${minutes} min)"
}

clip "Harbour Lights"       24 0x1b2a41 0xe39b4b 0x5a3d5c 196
clip "Night Drive"          18 0x0d1b2a 0x1b4965 0x62b6cb 147
clip "Northern Ridge"       12 0xd8d2c0 0x8fa68a 0x2d3a2e 220
clip "Coastline at Dusk"     9 0x2a1f3d 0xd9776b 0x3e6e7e 175
clip "Studio Session 04"     6 0x1d1c1a 0x6b5a45 0xc9b28f 262
clip "Field Notes, Kyoto"    4 0x3b1e1e 0xb5543c 0xe8c39e 294
clip "First Light"           3 0x10233b 0x7a9cc6 0xf2d6a2 330

# Render check: amber top half, blue bottom half. The visual review samples both halves to
# prove frames reach the screen the right way up.
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "color=c=0xE8A030:s=1280x720:r=24:d=20,drawbox=x=0:y=360:w=1280:h=360:color=0x2050C0:t=fill,format=yuv420p" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:d=20" -af volume=0.03 \
  -c:v libx264 -preset ultrafast -c:a aac "$out/Orientation Check.mp4"

# Two audio languages and two subtitle tracks, for the audio and subtitles menu.
cat > "$out/.night-market.srt" <<'SRT'
1
00:00:00,000 --> 00:01:00,000
The stalls opened just after the rain stopped.
SRT
cat > "$out/.night-market-fr.srt" <<'SRT'
1
00:00:00,000 --> 00:01:00,000
Les étals ouvrirent juste après la pluie.
SRT
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "gradients=s=${size}:r=${rate}:c0=0x1a1423:c1=0xc2593f:c2=0x2b4c59:n=3:speed=0.004,format=yuv420p" \
  -f lavfi -i "sine=frequency=220:sample_rate=48000" -f lavfi -i "sine=frequency=330:sample_rate=48000" \
  -i "$out/.night-market.srt" -i "$out/.night-market-fr.srt" -t 60 \
  -map 0 -map 1 -map 2 -map 3 -map 4 -af volume=0.03 \
  -c:v libx264 -preset ultrafast -crf 27 -c:a aac -b:a 48k -c:s srt \
  -metadata:s:a:0 language=eng -metadata:s:a:1 language=jpn -metadata:s:a:1 title="Commentary" \
  -metadata:s:s:0 language=eng -metadata:s:s:1 language=fre \
  "$out/Night Market.mkv"
rm -f "$out/.night-market.srt" "$out/.night-market-fr.srt"
# Loaded by hand through "Add subtitle file".
printf '1\n00:00:00,000 --> 00:01:00,000\nLoaded from a separate file.\n' > "$out/Night Market (extra).srt"

# Only a subtitle stream: a real Matroska file with nothing to play.
printf '1\n00:00:00,000 --> 00:00:05,000\nNo picture here.\n' > "$out/.only.srt"
ffmpeg -hide_banner -loglevel error -y -i "$out/.only.srt" -c:s srt "$out/Subtitles Only.mkv"
rm -f "$out/.only.srt"

# FFV1 in Matroska: Windows Media Foundation could not decode it (Milestone 1); libmpv can.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=s=640x360:r=12" -t 10 -c:v ffv1 "$out/Archive Scan.mkv"
# A file with a video extension but no video inside.
head -c 400000 /dev/urandom > "$out/Damaged Copy.mp4"
# Not a video at all.
printf 'Films to watch this winter\n' > "$out/Reading List.txt"

# A small library tree for the library screens. Names follow common conventions so the
# parser sorts them; two copies of one film at different sizes exercise copy detection, and
# "Untitled Project" has nothing to go on, so it is shown as needing a check.
lib="$out/Library"
libclip() { # relative-path WxH colour seconds
  local path="$lib/$1"
  mkdir -p "$(dirname "$path")"
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=$3:s=$2:r=12:d=$4,format=yuv420p" \
    -f lavfi -i "sine=frequency=262:sample_rate=48000:d=$4" -af volume=0.03 \
    -c:v libx264 -preset ultrafast -crf 30 -c:a aac -b:a 48k -shortest "$path"
}
libclip "Movies/Harbour Lights (2019)/Harbour Lights (2019) 720p.mp4" 1280x720 0x1b2a41 20
libclip "Movies/Harbour Lights (2019)/Harbour Lights (2019) 360p.mp4" 640x360 0x1b2a41 20
libclip "Movies/Night.Drive.2021.mp4" 960x540 0x1b4965 20
libclip "Movies/Paper Boats (2017).mp4" 960x540 0xd9776b 20
# Night Shift: three short episodes built like real ones, for intro, credits and autoplay.
# Each has its own cold open (10, 25 and 4 s), the same 20 s opening (bars and the same
# sound), its own story, and the same 25 s ending over black from 2:00 to the end at 2:25.
# Sounds are seeded noise shaped by a rhythm: identical where episodes share a part,
# different everywhere else.
episode() { # relative-path colour cold-open-seconds story-seconds seed
  local path="$lib/$1" colour=$2 cold=$3 story=$4 seed=$5
  local beat="volume='0.35+0.3*sin(2*PI*1.7*t)*sin(2*PI*0.23*t)':eval=frame"
  mkdir -p "$(dirname "$path")"
  ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=${colour}:s=960x540:r=12:d=${cold}" -f lavfi -i "anoisesrc=d=${cold}:c=pink:r=48000:a=0.4:seed=$((seed+1)),${beat}" \
    -f lavfi -i "smptehdbars=s=960x540:r=12:d=20" -f lavfi -i "anoisesrc=d=20:c=pink:r=48000:a=0.4:seed=900,${beat}" \
    -f lavfi -i "gradients=s=960x540:r=12:c0=${colour}:c1=0x1b2a41:speed=0.01:d=${story},format=yuv420p" -f lavfi -i "anoisesrc=d=${story}:c=pink:r=48000:a=0.4:seed=$((seed+2)),${beat}" \
    -f lavfi -i "color=c=black:s=960x540:r=12:d=25" -f lavfi -i "anoisesrc=d=25:c=brown:r=48000:a=0.4:seed=901,${beat}" \
    -filter_complex "[0:v]format=yuv420p[v0];[2:v]format=yuv420p[v1];[4:v]format=yuv420p[v2];[6:v]format=yuv420p[v3];[v0][1:a][v1][3:a][v2][5:a][v3][7:a]concat=n=4:v=1:a=1[v][a]" \
    -map "[v]" -map "[a]" -c:v libx264 -preset ultrafast -crf 30 -c:a aac -b:a 128k -movflags +faststart "$path"
}
episode "Shows/Night Shift/Season 1/Night Shift S01E01 Pilot.mp4" 0x3e6e7e 10 90 100
episode "Shows/Night Shift/Season 1/Night Shift S01E02 The Long Hour.mp4" 0x2a1f3d 25 75 200
episode "Shows/Night Shift/Season 1/Night Shift S01E03.mp4" 0x62b6cb 4 96 300
libclip "Personal/VID_20240612_181500.mp4" 960x540 0x8fa68a 20
libclip "Personal/2023-08-14 Beach day.mp4" 960x540 0xe8c39e 20
libclip "Personal/Untitled Project.mp4" 960x540 0x6b5a45 20
mkdir -p "$lib/Podcasts/Design Notes"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=196:sample_rate=44100:d=30" -af volume=0.03 \
  -c:a libmp3lame -b:a 64k "$lib/Podcasts/Design Notes/Design Notes Episode 12.mp3"
# Ignored by the scanner: a sample clip and a text file.
libclip "Movies/Harbour Lights (2019)/Sample/sample.mp4" 640x360 0x000000 3
printf 'notes\n' > "$lib/Movies/notes.txt"
echo "made the library tree"

ls -laR "$out"
