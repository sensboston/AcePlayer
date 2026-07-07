using System;
using System.Globalization;
using System.IO;

namespace AcePlayer
{
    /// <summary>Tiny key=value settings file kept next to the exe (last URL + live threshold).</summary>
    internal sealed class Settings
    {
        public string Url = "";
        public double ThresholdSeconds = 4.0;   // double.PositiveInfinity = "off" (never auto-jump)
        public double Volume = 1.0;             // 0..2
        public bool Muted = false;

        private static string FilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aceplayer.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    int i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    string k = raw.Substring(0, i).Trim();
                    string v = raw.Substring(i + 1).Trim();
                    if (k == "url") s.Url = v;
                    else if (k == "threshold")
                    {
                        if (v.Equals("off", StringComparison.OrdinalIgnoreCase))
                            s.ThresholdSeconds = double.PositiveInfinity;
                        else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                            s.ThresholdSeconds = t;
                    }
                    else if (k == "volume" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vv))
                        s.Volume = vv;
                    else if (k == "muted")
                        s.Muted = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                string thr = double.IsInfinity(ThresholdSeconds)
                    ? "off"
                    : ThresholdSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                string vol = Volume.ToString("0.###", CultureInfo.InvariantCulture);
                File.WriteAllText(FilePath,
                    $"url={Url}{Environment.NewLine}threshold={thr}{Environment.NewLine}" +
                    $"volume={vol}{Environment.NewLine}muted={(Muted ? "1" : "0")}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
