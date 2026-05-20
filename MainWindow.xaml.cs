using System.Windows;
using System.Windows.Controls;
using blink_o_mat.ViewModels;

namespace blink_o_mat
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            SourceInitialized += (_, _) => WindowTitleBarStyler.Apply(this);
        }

        private void FramesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FramesListView.SelectedItem is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() => FramesListView.ScrollIntoView(FramesListView.SelectedItem));
        }
    }
}