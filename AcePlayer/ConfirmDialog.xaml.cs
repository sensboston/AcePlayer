using System.Windows;
using System.Windows.Input;

namespace AcePlayer
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog() { InitializeComponent(); }

        public static bool Show(Window owner, string title, string message,
            string okText = "Register", string cancelText = "Cancel")
        {
            var d = new ConfirmDialog { Owner = owner };
            d.TitleText.Text = title;
            d.MessageText.Text = message;
            d.OkBtn.Content = okText;
            d.CancelBtn.Content = cancelText;
            return d.ShowDialog() == true;
        }

        private void OnOk(object sender, RoutedEventArgs e) { DialogResult = true; }
        private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) DialogResult = false;
            else if (e.Key == Key.Enter) DialogResult = true;
        }
        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        }
    }
}
