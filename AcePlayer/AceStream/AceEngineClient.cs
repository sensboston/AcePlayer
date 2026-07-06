using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AcePlayer.AceStream
{
    /// <summary>Resolved playback endpoint from the Ace Stream engine (or a passthrough direct URL).</summary>
    public sealed class AceStreamHandle
    {
        public string PlaybackUrl { get; set; }
        public string StatUrl { get; set; }
        public string CommandUrl { get; set; }
        public bool IsLive { get; set; }
        public bool IsDirect { get; set; }   // bypassed the engine (plain URL / file)
    }

    /// <summary>
    /// Thin client over the local Ace Stream engine HTTP API (default 127.0.0.1:6878).
    /// See https://docs.acestream.net/developers/ — /ace/getstream returns a playback_url
    /// (MPEG-TS over HTTP) plus stat_url (keep-alive) and command_url (stop).
    /// </summary>
    public sealed class AceEngineClient : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        private readonly string _host;
        private readonly int _port;
        private readonly string _pid = Guid.NewGuid().ToString();

        private AceStreamHandle _current;
        private Timer _keepAlive;

        public AceEngineClient(string host = "127.0.0.1", int port = 6878)
        {
            _host = host;
            _port = port;
        }

        private string Base => $"http://{_host}:{_port}";

        public async Task<bool> IsEngineRunningAsync()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    var r = await Http.GetAsync($"{Base}/webui/api/service?method=get_version", cts.Token);
                    return r.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Turns user input into a playable handle. Accepts:
        ///   http(s):// or a local file path  -> played directly (no engine)
        ///   acestream://&lt;id&gt; or 40-hex content id -> resolved via the engine (id=)
        ///   infohash:&lt;40-hex&gt;             -> resolved via the engine (infohash=)
        /// </summary>
        public async Task<AceStreamHandle> ResolveAsync(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (input.Length == 0) throw new ArgumentException("Empty source.");

            // An Ace engine getstream URL pasted directly — resolve it via the JSON API so we get
            // stat_url/command_url (keep-alive) and treat it as a proper engine session, not a raw
            // "direct" URL (which the engine would tear down without keep-alive polling).
            if (input.IndexOf("/ace/getstream", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string u = input;
                if (u.IndexOf("format=", StringComparison.OrdinalIgnoreCase) < 0)
                    u += (u.Contains("?") ? "&" : "?") + "format=json";
                return await ResolveViaEngineJson(u);
            }

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                System.IO.File.Exists(input))
            {
                _current = new AceStreamHandle { PlaybackUrl = input, IsLive = true, IsDirect = true };
                return _current;
            }

            // Protocol handlers hand us e.g. "acestream://<id>/" — Windows appends a trailing slash
            // to the authority-only URL. Strip trailing slashes/whitespace so the id stays valid.
            input = input.TrimEnd('/', ' ', '\t');

            string query;
            if (input.StartsWith("acestream://", StringComparison.OrdinalIgnoreCase))
                query = "id=" + Uri.EscapeDataString(input.Substring("acestream://".Length).TrimEnd('/').Trim());
            else if (input.StartsWith("infohash:", StringComparison.OrdinalIgnoreCase))
                query = "infohash=" + Uri.EscapeDataString(input.Substring("infohash:".Length).Trim());
            else if (Regex.IsMatch(input, "^[0-9a-fA-F]{40}$"))
                query = "id=" + input;
            else
                query = "url=" + Uri.EscapeDataString(input);   // .acelive / .torrent url

            return await ResolveViaEngineJson($"{Base}/ace/getstream?{query}&format=json&pid={_pid}");
        }

        private async Task<AceStreamHandle> ResolveViaEngineJson(string jsonUrl)
        {
            string json;
            using (var resp = await Http.GetAsync(jsonUrl))
            {
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync();
            }

            string error = Extract(json, "error");
            if (!string.IsNullOrEmpty(error) && error != "null")
                throw new InvalidOperationException("Engine error: " + error);

            var handle = new AceStreamHandle
            {
                PlaybackUrl = Extract(json, "playback_url"),
                StatUrl = Extract(json, "stat_url"),
                CommandUrl = Extract(json, "command_url"),
                IsLive = ExtractInt(json, "is_live") != 0,
            };
            if (string.IsNullOrEmpty(handle.PlaybackUrl))
                throw new InvalidOperationException("Engine returned no playback_url.");

            _current = handle;
            StartKeepAlive(handle);
            return handle;
        }

        /// <summary>A snapshot of the engine's stat for a session.</summary>
        public sealed class AceStat
        {
            public string Status = "";
            public long Downloaded;
            public int Peers;
            /// <summary>Playback URL should serve data once the engine is downloading.</summary>
            public bool Playable => Downloaded > 0 || Status == "dl";
        }

        public async Task<AceStat> GetStatAsync(AceStreamHandle handle)
        {
            if (handle == null || string.IsNullOrEmpty(handle.StatUrl)) return null;
            try
            {
                string json = await Http.GetStringAsync(handle.StatUrl);
                return new AceStat
                {
                    Status = Extract(json, "status") ?? "",
                    Downloaded = ExtractLong(json, "downloaded"),
                    Peers = ExtractInt(json, "peers"),
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Polls the engine until the stream is actually delivering data (or the timeout elapses).
        /// A dead/not-broadcasting stream stays in "prebuf" with downloaded=0 and returns false.
        /// </summary>
        public async Task<bool> WaitForReadyAsync(AceStreamHandle handle, Action<AceStat> onUpdate,
            int timeoutSeconds, CancellationToken ct)
        {
            if (handle == null || handle.IsDirect || string.IsNullOrEmpty(handle.StatUrl)) return true;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                var s = await GetStatAsync(handle);
                if (s != null)
                {
                    onUpdate?.Invoke(s);
                    if (s.Playable) return true;
                }
                await Task.Delay(700, ct);
            }
            return false;
        }

        private void StartKeepAlive(AceStreamHandle handle)
        {
            StopKeepAlive();
            if (string.IsNullOrEmpty(handle.StatUrl)) return;
            _keepAlive = new Timer(async _ =>
            {
                try { await Http.GetAsync(handle.StatUrl); } catch { }
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        }

        private void StopKeepAlive()
        {
            _keepAlive?.Dispose();
            _keepAlive = null;
        }

        /// <summary>Tell the engine to stop the current session (frees the P2P download).</summary>
        public async Task StopAsync()
        {
            StopKeepAlive();
            var h = _current;
            _current = null;
            if (h == null || h.IsDirect || string.IsNullOrEmpty(h.CommandUrl)) return;
            try { await Http.GetAsync(h.CommandUrl + "?method=stop"); } catch { }
        }

        public void Dispose()
        {
            StopKeepAlive();
            try { StopAsync().Wait(2000); } catch { }
        }

        // --- tiny JSON field extraction (engine responses are flat and predictable) ---

        private static string Extract(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return Regex.Unescape(m.Groups[1].Value);
            // non-quoted (number / null)
            m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*([^,}\\s]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int ExtractInt(string json, string key)
        {
            var v = Extract(json, key);
            return int.TryParse(v, out var n) ? n : 0;
        }

        private static long ExtractLong(string json, string key)
        {
            var v = Extract(json, key);
            return long.TryParse(v, out var n) ? n : 0;
        }
    }
}
