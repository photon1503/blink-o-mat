using System.Windows;

namespace blink_o_mat
{
    public partial class RejectConfirmDialog : Window
    {
        public RejectConfirmDialog(int frameCount, string destination)
        {
            InitializeComponent();
            FrameCountText.Text = $"{frameCount} frame{(frameCount == 1 ? "" : "s")}";
            DestinationText.Text = destination;
        }

        private void Proceed_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
