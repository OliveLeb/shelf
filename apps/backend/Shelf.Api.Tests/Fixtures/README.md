# Miniature fixtures

Synthetic, redistributable test content, generated for Shelf (no personal data):

- `photo.jpg`: 960 × 640 orange/blue gradient.
- `photo.heic`: the same gradient, HEVC-encoded HEIC (not AVIF with a HEIC extension).
- `video.mp4`: one second of blue, 640 × 360, H.264 with an MP4 index at the front.

Recreate from the repository root with ImageMagick + libheif and ffmpeg:

```sh
convert -size 960x640 gradient:'#ff6600-#003399' apps/backend/Shelf.Api.Tests/Fixtures/photo.jpg
convert apps/backend/Shelf.Api.Tests/Fixtures/photo.jpg -define heic:speed=10 apps/backend/Shelf.Api.Tests/Fixtures/photo.heic
ffmpeg -y -v error -f lavfi -i 'color=c=blue:s=640x360:d=1:r=10' \
  -c:v libx264 -pix_fmt yuv420p -movflags +faststart apps/backend/Shelf.Api.Tests/Fixtures/video.mp4
```

Tests upload these versioned bytes to real MinIO, decode the resulting JPEG to
check its dimensions, and compare the original download byte for byte.
