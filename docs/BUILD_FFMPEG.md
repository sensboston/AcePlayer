# Rebuilding the trimmed FFmpeg

The prebuilt native DLLs in `AcePlayer/native/` are a minimal FFmpeg **6.1** shared build:
H.264/HEVC/MPEG‑2 video, AAC/MP2/MP3/AC3/E‑AC3 audio, `mpegts`/`hls` demuxers, the `bwdif`
deinterlacer, and `http`/`tls` protocols. All the DLLs together are ~4 MB (vs ~130 MB for a full
build). You only need to rebuild them to change the FFmpeg version or add codecs.

From an MSYS2 / Git Bash shell (no compiler is required up front, the script fetches one):

```sh
mkdir -p /c/build && cd /c/build

# toolchain: WinLibs GCC (UCRT) + NASM
curl -sL -o winlibs.zip "https://github.com/brechtsanders/winlibs_mingw/releases/download/14.2.0posix-12.0.0-ucrt-r3/winlibs-x86_64-posix-seh-gcc-14.2.0-mingw-w64ucrt-12.0.0-r3.zip"
curl -sL -o nasm.zip     "https://www.nasm.us/pub/nasm/releasebuilds/2.16.01/win64/nasm-2.16.01-win64.zip"
unzip -q winlibs.zip -d /c/build
unzip -q nasm.zip    -d /c/build
git clone --depth 1 --branch n6.1.1 https://github.com/FFmpeg/FFmpeg.git ffmpeg

export PATH="/c/build/mingw64/bin:/c/build/nasm-2.16.01:/usr/bin:/bin"
export TMPDIR=/c/build/tmp TEMP=/c/build/tmp TMP=/c/build/tmp   # avoid Windows %TEMP% path issues
mkdir -p "$TMPDIR"
cd /c/build/ffmpeg

./configure \
  --prefix=/c/build/out \
  --enable-shared --disable-static \
  --disable-programs --disable-doc \
  --enable-gpl --enable-avdevice --enable-postproc \
  --disable-autodetect --enable-small --disable-debug \
  --enable-x86asm --enable-w32threads --enable-schannel --enable-network \
  --disable-everything \
  --enable-protocol=file,http,tcp,tls,https,crypto,httpproxy,hls,data,pipe \
  --enable-demuxer=mpegts,hls,mov,matroska,flv,aac,mp3,ac3,h264,hevc,data \
  --enable-parser=h264,hevc,aac,aac_latm,ac3,mpegaudio,mpegvideo \
  --enable-decoder=h264,hevc,mpeg2video,aac,aac_latm,mp2,mp2float,mp3,ac3,eac3,pcm_s16le,pcm_s16be \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,aac_adtstoasc,extract_extradata,null \
  --enable-filter=buffer,buffersink,bwdif,yadif,format,scale,null \
  --enable-muxer=mpegts,null

mingw32-make -j8 && mingw32-make install
# then copy the DLLs from /c/build/out/bin into AcePlayer/native/
```

Copy these into `AcePlayer/native/`:
`avutil-58.dll avcodec-60.dll avformat-60.dll avdevice-60.dll avfilter-9.dll swscale-7.dll
swresample-4.dll postproc-57.dll libwinpthread-1.dll`

## Notes learned the hard way

- `--enable-protocol=...` must come **after** `--disable-everything`, or `--disable-everything`
  wipes the protocols again.
- FFmpeg.AutoGen 6.1 binds all eight libraries (including `avdevice` and `postproc`), so all must be
  present even though the app never calls them. `postproc` requires `--enable-gpl`, which is why this
  build is GPL.
- `avutil` (built with the posix‑threads GCC) links `libwinpthread-1.dll`; it is bundled next to the
  `av*` DLLs, and the app calls `SetDllDirectory` on the extraction folder so `LoadLibrary` resolves
  that sibling dependency.
- The app sets `DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = false` so any function the
  trimmed build omits simply isn't bound (it is never called).
