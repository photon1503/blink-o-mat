using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using blink_o_mat.Services;
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
            WindowPlacementService.RestoreMainWindow(this);
            Closing += MainWindow_Closing;
        }

        private void RejectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;

            var filterCounts = vm.GetRejectedCountByFilter();

            var dialog = new RejectConfirmDialog(vm.RejectedFrameCount, vm.RejectedFolder, filterCounts)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                vm.ExecuteMoveRejected(dialog.SelectedFilterKeys);
            }
        }

        private void FramesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FramesListView.SelectedItem is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() => FramesListView.ScrollIntoView(FramesListView.SelectedItem));
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            WindowPlacementService.SaveMainWindow(this);
        }
    }
}