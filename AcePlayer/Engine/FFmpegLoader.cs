using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace AcePlayer.Engine
{
    /// <summary>
    /// Makes the native FFmpeg 6.x libraries available to FFmpeg.AutoGen.
    ///
    /// In the portable single-file build the DLLs are embedded as resources and extracted to a
    /// versioned folder under %temp% on first run (skipped if already present with the right size).
    /// For a plain dev build we fall back to probing well-known locations on disk.
    /// </summary>
    internal static class FFmpegLoader
    {
        private const string ResourcePrefix = "AcePlayer.NativeFFmpeg.";

        private static bool _initialized;
        private static readonly object _gate = new object();

        private static readonly string[] CandidateDirs =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg"),
            @"C:\Program Files\ffmpeg",
            @"C:\Program Files\ffmpeg\bin",
            @"C:\ffmpeg\bin",
        };

        public static string RootPath { get; private set; }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_gate)
            {
                if (_initialized) return;

                string root = ExtractEmbedded() ?? ProbeDisk();
                if (root == null)
                    throw new DllNotFoundException(
                        "Не найдены нативные библиотеки FFmpeg 6.x (avformat-60.dll и др.).");

                // LoadLibrary(fullpath) resolves a DLL's own imports (e.g. avutil -> libwinpthread)
                // using the exe's directory, not the DLL's. Add our folder to the search path so
                // the sibling runtime DLLs are found.
                SetDllDirectory(root);

                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = false;

                ffmpeg.RootPath = root;
                RootPath = root;

                var version = ffmpeg.av_version_info();          // force-load & verify
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                _initialized = true;
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] loaded {version} from {root}");
            }
        }

        /// <summary>Extracts embedded native DLLs to %temp%. Returns the folder, or null if none embedded.</summary>
        private static string ExtractEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resources = Array.FindAll(asm.GetManifestResourceNames(),
                n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                     n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (resources.Length == 0) return null;

            // Version the folder by assembly version so a new build never reuses stale DLLs.
            string ver = asm.GetName().Version?.ToString() ?? "0";
            string dir = Path.Combine(Path.GetTempPath(), "AcePlayer_ffmpeg", ver);
            Directory.CreateDirectory(dir);

            foreach (var res in resources)
            {
                string fileName = res.Substring(ResourcePrefix.Length);   // e.g. "avcodec-60.dll"
                string target = Path.Combine(dir, fileName);

                using (var stream = asm.GetManifestResourceStream(res))
                {
                    if (stream == null) continue;
                    // Skip re-extraction if the file already matches in size.
                    if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
                        continue;

                    string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        stream.CopyTo(fs);
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);
                    }
                    catch (IOException)
                    {
                        // Another instance won the race; the existing file is fine.
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            return dir;
        }

        private static string ProbeDisk()
        {
            foreach (var d in CandidateDirs)
                if (Directory.Exists(d) && File.Exists(Path.Combine(d, "avformat-60.dll")))
                    return d;
            return null;
        }

        public static unsafe string DescribeError(int code)
        {
            const int bufSize = 1024;
            byte* buf = stackalloc byte[bufSize];
            ffmpeg.av_strerror(code, buf, bufSize);
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {code}";
        }
    }
}
