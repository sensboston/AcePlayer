using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AcePlayer.AceStream;
using AcePlayer.Audio;
using AcePlayer.Decoding;

namespace AcePlayer
{
    public partial class MainWindow : Window
    {
        private readonly AceEngineClient _engine = new AceEngineClient();
        private MediaDecoder _decoder;
        private FrameBuffer _frame;
        private PresentationClock _clock;
        private PcmQueue _pcm;
        private AudioPlayer _audio;
        private CancellationTokenSource _playCts;
        private bool _deinterlaceEnabled = true;

        // Live-edge control: once accumulated freeze-lag exceeds this, jump back to the live edge.
        private double _liveThreshold = 4.0;
        private bool _isLivePlayback;
        private bool _autoSized;
        private readonly Settings _settings = Settings.Load();

        private bool _fullscreen;
        private WindowState _savedState;
        private WindowStyle _savedStyle;

        private readonly DispatcherTimer _stats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private long _lastPresented;

        private readonly DispatcherTimer _idleTimer =
            new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private bool _controlsVisible = true;

        // Keep the display and machine awake while playing.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;
        private void KeepAwake(bool on) =>
            SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) : ES_CONTINUOUS);

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"AcePlayer {v.Major}.{v.Minor}.{v.Build}";
            _stats.Tick += OnStatsTick;
            _stats.Start();
            _idleTimer.Tick += OnIdleTick;
            _idleTimer.Start();
            Closing += (s, e) => TeardownPlayback();

            SelectThreshold(_settings.ThresholdSeconds);

            Volume.Init(_settings.Volume, _settings.Muted);
            Volume.Changed += OnVolumeChanged;

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                SourceBox.Text = args[1];
                Loaded += async (s, e) => await PlayAsync(SourceBox.Text);
            }
            else
            {
                SourceBox.Text = _settings.Url;   // last used source
            }
        }

        private void SelectThreshold(double thr)
        {
            string target = double.IsInfinity(thr)
                ? "Off"
                : ((int)Math.Round(thr)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
            foreach (System.Windows.Controls.ComboBoxItem it in ThresholdCombo.Items)
                if (string.Equals(it.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
                { ThresholdCombo.SelectedItem = it; return; }
            _liveThreshold = thr;   // not in the list — keep the value, leave combo unselected
        }

        private void OnThresholdChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!(ThresholdCombo.SelectedItem is System.Windows.Controls.ComboBoxItem it)) return;
            string s = (it.Content?.ToString() ?? "").Trim();
            double v = s.Equals("Off", StringComparison.OrdinalIgnoreCase)
                ? double.PositiveInfinity
                : (double.TryParse(s.TrimEnd('s', 'S'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 4.0);
            _liveThreshold = v;
            _settings.ThresholdSeconds = v;
            _settings.Save();
        }

        // ---- source input ----

        private bool _playbackActive;

        private void SetPlaybackActive(bool active)
        {
            _playbackActive = active;
            PlayButton.Content = active ? "■ Stop" : "▶ Play";
        }

        private async void OnPlayClick(object sender, RoutedEventArgs e)
        {
            if (_playbackActive)
            {
                TeardownPlayback();
                ShowCenter("Stopped.");
            }
            else
            {
                await PlayAsync(SourceBox.Text);
            }
        }

        private void OnVolumeChanged()
        {
            if (_pcm != null) _pcm.Volume = Volume.EffectiveVolume;
            _settings.Volume = Volume.Volume;
            _settings.Muted = Volume.Muted;
            _settings.Save();
        }

        private async void OnSourceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await PlayAsync(SourceBox.Text);
        }

        private void OnSourceTextChanged(object sender, TextChangedEventArgs e)
        {
            ClearButton.Visibility = string.IsNullOrEmpty(SourceBox.Text)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnClearSource(object sender, RoutedEventArgs e)
        {
            SourceBox.Clear();
            SourceBox.Focus();
        }

        private void OnRegisterClick(object sender, RoutedEventArgs e)
        {
            bool already = Engine.RegistrationHelper.IsRegistered();
            string msg = (already
                ? "AcePlayer is already registered. Re-register to point Ace Stream at this copy of the app?\n\n"
                : "Register AcePlayer as the handler for acestream:// links and .acelive / .acestream files?\n\n")
                + "This writes to the current user's registry only (no administrator rights). "
                + "After this, clicking an Ace Stream link opens it here.";

            if (!ConfirmDialog.Show(this, "Register Ace Stream handler", msg,
                    already ? "Re-register" : "Register", "Cancel"))
                return;

            try
            {
                Engine.RegistrationHelper.Register();
                ShowCenter("Registered as the Ace Stream handler.");
            }
            catch (Exception ex)
            {
                ShowCenter("Registration failed: " + ex.Message);
            }
        }

        // ---- playback ----

        private async System.Threading.Tasks.Task PlayAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) { ShowCenter("Enter a source."); return; }

            _settings.Url = source.Trim();
            _settings.Save();

            TeardownPlayback();
            SetPlaybackActive(true);      // button becomes Stop for the whole open/play lifetime
            _playCts = new CancellationTokenSource();
            var token = _playCts.Token;
            ShowCenter("Resolving source…");

            AceStreamHandle handle;
            try { handle = await _engine.ResolveAsync(source); }
            catch (Exception ex) { ShowCenter("Error: " + ex.Message); SetPlaybackActive(false); return; }

            if (!handle.IsDirect)
            {
                bool ready;
                try
                {
                    ready = await _engine.WaitForReadyAsync(handle,
                        s => ShowCenter($"Buffering: {s.Status}, {s.Peers} peers, {s.Downloaded / 1024} KB"),
                        timeoutSeconds: 40, ct: token);
                }
                catch (OperationCanceledException) { return; }

                if (!ready)
                {
                    ShowCenter("Stream is not delivering data (prebuf, 0 bytes) — maybe it is not broadcasting now.");
                    TeardownPlayback();
                    return;
                }
            }
            ShowCenter("Opening…");

            _isLivePlayback = handle.PlaybackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            _frame = new FrameBuffer(capacityFrames: 60);
            _clock = new PresentationClock();

            int bytesPerSecond = MediaDecoder.AudioSampleRate * MediaDecoder.AudioChannels * 2;
            _pcm = new PcmQueue(
                new NAudio.Wave.WaveFormat(MediaDecoder.AudioSampleRate, 16, MediaDecoder.AudioChannels),
                capacityBytes: bytesPerSecond / 2); // ~500 ms

            _pcm.Volume = Volume.EffectiveVolume;

            _decoder = new MediaDecoder(handle.PlaybackUrl, _frame, _pcm, deinterlace: _deinterlaceEnabled);
            _decoder.AutoLive = false;     // live-edge is driven here by accumulated-lag threshold
            _decoder.Failed += msg => Dispatcher.Invoke(() => { ShowCenter("Error: " + msg); SetPlaybackActive(false); });
            _decoder.Ended += () => Dispatcher.Invoke(() => { ShowCenter("Stream ended."); SetPlaybackActive(false); });
            _decoder.Reconnecting += n => Dispatcher.Invoke(() => ShowCenter($"Reconnecting ({n})…"));
            _decoder.VideoSizeChanged += (w, h) => Dispatcher.Invoke(() => { HideCenter(); ResizeToVideo(w, h); });

            _audio = new AudioPlayer(_pcm);
            _audio.Start();

            _autoSized = false;
            Video.Attach(_frame, _clock);
            _decoder.Start();
            KeepAwake(true);   // no screensaver / sleep while playing

            // The ad-supported Ace engine opens a betting-ad browser window on stream start; close it.
            if (!handle.IsDirect)
                Engine.AdWindowCloser.CloseFor(8);
        }

        /// <summary>Fit the window to the video's aspect ratio (once per playback, windowed only).</summary>
        private void ResizeToVideo(int w, int h)
        {
            if (_autoSized || _fullscreen || w <= 0 || h <= 0) return;
            _autoSized = true;

            var wa = SystemParameters.WorkArea;
            double scale = Math.Min(Math.Min(wa.Width * 0.85 / w, wa.Height * 0.85 / h), 1.0);
            double clientW = w * scale, clientH = h * scale;

            double chromeW = ActualWidth - VideoArea.ActualWidth;    // borders
            double chromeH = ActualHeight - VideoArea.ActualHeight;   // title bar + borders
            Width = clientW + chromeW;
            Height = clientH + chromeH;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        private void OnLiveClick(object sender, RoutedEventArgs e) => JumpToLive();

        /// <summary>Discard the accumulated backlog and reopen at the engine's live edge.</summary>
        private void JumpToLive()
        {
            if (_decoder == null) return;
            _frame?.Flush();
            _pcm?.Flush();
            _decoder.GoLive();
            Video.ResetLag();
        }

        private void TeardownPlayback()
        {
            SetPlaybackActive(false);
            KeepAwake(false);
            try { _playCts?.Cancel(); } catch { }
            try { _playCts?.Dispose(); } catch { }
            _playCts = null;
            Video.Detach();
            try { _frame?.Close(); } catch { }
            try { _decoder?.Dispose(); } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _pcm?.Close(); } catch { }
            try { _clock?.Stop(); } catch { }
            try { _engine.StopAsync().Wait(1500); } catch { }
            _decoder = null; _audio = null; _pcm = null; _frame = null; _clock = null;
            _isLivePlayback = false;
            TimeText.Text = "live";
        }

        // ---- fullscreen ----

        private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ToggleFullscreen();
        }

        private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool typing = SourceBox.IsKeyboardFocused;
            if (e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
            else if (!typing && (e.Key == Key.F || e.Key == Key.F11)) ToggleFullscreen();
            else if (!typing && e.Key == Key.L) OnLiveClick(this, null);
            else if (!typing && e.Key == Key.Space) OnPlayClick(this, null);
            ShowControls();
        }

        private void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _savedState = WindowState;
                _savedStyle = WindowStyle;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                _fullscreen = true;
            }
            else
            {
                WindowStyle = _savedStyle;
                ResizeMode = ResizeMode.CanResize;
                WindowState = _savedState;
                _fullscreen = false;
            }
            ShowControls();
        }

        // ---- auto-hiding controls & cursor ----

        private void OnUserActivity(object sender, MouseEventArgs e) => ShowControls();

        private void OnIdleTick(object sender, EventArgs e)
        {
            if (TopBar.IsMouseOver || Volume.IsMouseOver || SourceBox.IsKeyboardFocused) return;
            HideControls();
        }

        private void ShowControls()
        {
            _idleTimer.Stop();
            _idleTimer.Start();
            Cursor = Cursors.Arrow;
            if (_controlsVisible) return;
            _controlsVisible = true;
            Fade(TopBar, 1.0);
            Fade(StatsText, 1.0);
            Fade(Volume, 1.0);
        }

        private void HideControls()
        {
            if (!_controlsVisible) return;
            _controlsVisible = false;
            Fade(TopBar, 0.0);
            Fade(StatsText, 0.0);
            Fade(Volume, 0.0);
            if (_fullscreen) Cursor = Cursors.None;
        }

        private static void Fade(UIElement element, double to)
        {
            element.BeginAnimation(OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)) { FillBehavior = FillBehavior.HoldEnd });
        }

        // ---- status / stats ----

        private void ShowCenter(string text)
        {
            CenterStatus.Text = text;
            CenterStatus.Visibility = Visibility.Visible;
        }

        private void HideCenter() => CenterStatus.Visibility = Visibility.Collapsed;

        private void OnStatsTick(object sender, EventArgs e)
        {
            if (_decoder == null) { StatsText.Text = ""; return; }
            long now = Video.PresentedFrames;
            long fps = now - _lastPresented;
            _lastPresented = now;
            int audioMs = _pcm == null ? 0 : (int)(_pcm.BufferedBytes /
                (double)(MediaDecoder.AudioSampleRate * MediaDecoder.AudioChannels * 2) * 1000);
            StatsText.Text = $"{fps} fps   audio {audioMs} ms";

            UpdateLive();
        }

        private void UpdateLive()
        {
            if (_decoder == null || !_isLivePlayback) { TimeText.Text = "live"; return; }

            double lag = Video.LagSeconds;
            TimeText.Text = lag < 1.5 ? "live" : $"-{lag:0}s";

            // Threshold policy: don't chase small drift; once the accumulated freeze-lag really
            // exceeds the bound, jump back to the live edge in one move.
            if (lag > _liveThreshold) JumpToLive();
        }
    }
}
