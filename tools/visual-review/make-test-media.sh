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

# A real video whose codec (FFV1 in Matroska) Windows Media Foundation cannot decode.
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=s=640x360:r=12" -t 10 -c:v ffv1 "$out/Archive Scan.mkv"
# A file with a video extension but no video inside.
head -c 400000 /dev/urandom > "$out/Damaged Copy.mp4"
# Not a video at all.
printf 'Films to watch this winter\n' > "$out/Reading List.txt"

ls -la "$out"
