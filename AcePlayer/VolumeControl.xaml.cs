using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AcePlayer
{
    /// <summary>VLC-style volume: a speaker mute toggle plus a triangular gradient slider (0..125%).</summary>
    public partial class VolumeControl : UserControl
    {
        private const double Max = 2.0;
        private const double WedgeWidth = 86.0;   // matches the triangle geometry

        // Segoe MDL2 Assets glyphs, by code point (E767 = Volume, E74F = Mute).
        private static readonly string GlyphVolume = ((char)0xE767).ToString();
        private static readonly string GlyphMute = ((char)0xE74F).ToString();

        public double Volume { get; private set; } = 1.0;
        public bool Muted { get; private set; }
        public double EffectiveVolume => Muted ? 0.0 : Volume;

        /// <summary>Raised whenever the volume or mute state changes.</summary>
        public event Action Changed;

        public VolumeControl()
        {
            InitializeComponent();
            Refresh();
        }

        public void Init(double volume, bool muted)
        {
            Volume = Clamp(volume);
            Muted = muted;
            Refresh();
        }

        private void OnSpeaker(object sender, RoutedEventArgs e)
        {
            Muted = !Muted;
            Refresh();
            Changed?.Invoke();
        }

        private void OnWedgeDown(object sender, MouseButtonEventArgs e)
        {
            Wedge.CaptureMouse();
            SetFromMouse(e.GetPosition(Wedge).X);
        }

        private void OnWedgeMove(object sender, MouseEventArgs e)
        {
            if (Wedge.IsMouseCaptured) SetFromMouse(e.GetPosition(Wedge).X);
        }

        private void OnWedgeUp(object sender, MouseButtonEventArgs e) => Wedge.ReleaseMouseCapture();

        private void SetFromMouse(double x)
        {
            Volume = Clamp(x / WedgeWidth * Max);
            Muted = false;
            Refresh();
            Changed?.Invoke();
        }

        private static double Clamp(double v) => v < 0 ? 0 : (v > Max ? Max : v);

        private void Refresh()
        {
            double shown = Muted ? 0.0 : Volume;
            FgClip.Rect = new Rect(0, 0, shown / Max * WedgeWidth, 28);
            PctText.Text = Muted ? "muted" : ((int)Math.Round(Volume * 100)) + "%";
            Speaker.Content = (Muted || Volume <= 0.001) ? GlyphMute : GlyphVolume;
        }
    }
}
