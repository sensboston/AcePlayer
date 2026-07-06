using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace AcePlayer.Engine
{
    /// <summary>
    /// The (ad-supported) Ace Stream engine pops open a browser window with a betting ad when a
    /// stream starts. This watches top-level browser windows for a short while after playback begins
    /// and closes the ad window — matched either by a title marker or, more robustly (the ad domain
    /// rotates), by its page content ("Silence is golden") via UI Automation. Scoped to browser
    /// window classes and specific markers so it never touches unrelated windows.
    /// </summary>
    internal static class AdWindowCloser
    {
        // Lowercased substrings identifying the ad by the browser window title (URL-derived).
        private static readonly string[] TitleMarkers =
        {
            "parimatch", "pmwin", "1xbet", "melbet", "betwinner", "1win", "mostbet", "silence is golden",
        };

        // Distinctive page-content strings (matched via UI Automation, domain-independent).
        private static readonly string[] ContentMarkers =
        {
            "Silence is golden",
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern IntPtr PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private const uint WM_CLOSE = 0x0010;
        private static bool _logCandidates;

        /// <summary>Watch for and close ad windows for a short window after a stream starts.</summary>
        public static void CloseFor(double seconds = 8.0)
        {
            _logCandidates = true;   // dump seen browser titles once, to help tune markers
            var t = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    try { Scan(); } catch { }
                    Thread.Sleep(500);
                }
            })
            { IsBackground = true, Name = "AdWindowCloser" };
            t.Start();
        }

        private static void Scan()
        {
            bool logThisPass = _logCandidates;
            _logCandidates = false;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var cls = new StringBuilder(64);
                GetClassNameW(hWnd, cls, cls.Capacity);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;   // Chrome / Edge / Chromium

                var titleSb = new StringBuilder(512);
                GetWindowTextW(hWnd, titleSb, titleSb.Capacity);
                string title = titleSb.ToString();
                string tl = title.ToLowerInvariant();
                if (logThisPass) Log("candidate: " + title);

                bool match = false;
                foreach (var m in TitleMarkers)
                    if (tl.Contains(m)) { match = true; break; }

                if (!match && MatchesContent(hWnd))
                    match = true;

                if (match)
                {
                    PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Log("closed: " + title);
                }
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>Look for the ad's distinctive page text in the window's accessibility tree.</summary>
        private static bool MatchesContent(IntPtr hWnd)
        {
            try
            {
                var el = AutomationElement.FromHandle(hWnd);
                if (el == null) return false;
                foreach (var phrase in ContentMarkers)
                {
                    var cond = new PropertyCondition(AutomationElement.NameProperty, phrase);
                    if (el.FindFirst(TreeScope.Descendants, cond) != null) return true;
                }
            }
            catch { }
            return false;
        }

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "aceplayer_ads.log"),
                    $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
