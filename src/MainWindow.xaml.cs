using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using blink_o_mat.Services;
using blink_o_mat.ViewModels;

namespace blink_o_mat
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _closeSessionPanelAfterLoadRequested;
        private bool _sessionLoadInProgress;
        private bool _reopenSessionPanelOnActivate;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += Vm_PropertyChanged;
            }
            SourceInitialized += (_, _) => WindowTitleBarStyler.Apply(this);
            Activated += MainWindow_Activated;
            WindowPlacementService.RestoreMainWindow(this);
            Closing += MainWindow_Closing;
            Title = BuildTitle();
        }

        private static string BuildTitle()
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            // Version 1.0.0.0 is the default (unstamped dev build) — omit it.
            if (v is null || v == new Version(1, 0, 0, 0))
                return "Rejector";
            return $"Rejector {v.Major}.{v.Minor}.{v.Build}";
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

        private void SessionSettingsButtonTopBar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.ContextMenu is System.Windows.Controls.ContextMenu menu)
            {
                menu.PlacementTarget = button;
                menu.IsOpen = true;
            }
        }

        private void SessionSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.ContextMenu is System.Windows.Controls.ContextMenu menu)
            {
                menu.PlacementTarget = button;
                menu.IsOpen = true;
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
            Activated -= MainWindow_Activated;
            WindowPlacementService.SaveMainWindow(this);
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged -= Vm_PropertyChanged;
                vm.Performance.Dispose();
                vm.StopFolderWatch();
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var hasCtrlAlt = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            hasCtrlAlt = hasCtrlAlt && (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt));

            if (key == Key.W && hasCtrlAlt)
            {
                if (DataContext is MainViewModel vm && vm.DebugShowUpdateBannerCommand.CanExecute(null))
                {
                    vm.DebugShowUpdateBannerCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel vm)
            {
                return;
            }

            if (e.PropertyName != nameof(MainViewModel.IsBusy))
            {
                return;
            }

            if (vm.IsBusy)
            {
                if (_closeSessionPanelAfterLoadRequested)
                {
                    _sessionLoadInProgress = true;
                }
                return;
            }

            if (_closeSessionPanelAfterLoadRequested
                && _sessionLoadInProgress
                && vm.TotalFrameCount > 0
                && SessionContextMenu?.IsOpen == true)
            {
                Dispatcher.BeginInvoke(() => SessionContextMenu.IsOpen = false);
            }

            _closeSessionPanelAfterLoadRequested = false;
            _sessionLoadInProgress = false;
        }

        private void LoadFramesButtonPopup_Click(object sender, RoutedEventArgs e)
        {
            _closeSessionPanelAfterLoadRequested = true;
            _sessionLoadInProgress = false;
        }

        private void SessionPopupBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            _reopenSessionPanelOnActivate = true;
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            if (!_reopenSessionPanelOnActivate)
            {
                return;
            }

            _reopenSessionPanelOnActivate = false;
            Dispatcher.BeginInvoke(() =>
            {
                if (SessionSettingsButtonTopBar?.ContextMenu is System.Windows.Controls.ContextMenu menu)
                {
                    menu.PlacementTarget = SessionSettingsButtonTopBar;
                    menu.IsOpen = true;
                }
            });
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsSettingsOverlayOpen = false;
            }
        }

        private void CreateNewProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
            {
                return;
            }

            var inputWindow = new Window
            {
                Title = "Create new profile",
                Owner = this,
                Width = 360,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("PanelBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };

            var inputTextBox = new System.Windows.Controls.TextBox
            {
                Margin = new Thickness(0, 8, 0, 12),
                MinWidth = 280,
                Text = vm.SelectedProfileName
            };

            var ok = new System.Windows.Controls.Button { Content = "Create", Width = 88, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 88, IsCancel = true };

            ok.Click += (_, _) => inputWindow.DialogResult = true;

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock { Text = "Profile name" });
            root.Children.Add(inputTextBox);
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            actions.Children.Add(ok);
            actions.Children.Add(cancel);
            root.Children.Add(actions);

            inputWindow.Content = root;

            if (inputWindow.ShowDialog() == true)
            {
                var created = vm.TryCreateSettingsProfile(inputTextBox.Text);
                if (!created)
                {
                    System.Windows.MessageBox.Show(this, "A profile with this name already exists.", "Profile exists", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}
