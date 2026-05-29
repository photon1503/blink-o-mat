using System.Windows;
using System.Windows.Controls;
using blink_o_mat.ViewModels;

namespace blink_o_mat.Views
{
    public partial class SettingsOverlayView : System.Windows.Controls.UserControl
    {
        public SettingsOverlayView()
        {
            InitializeComponent();
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
                Owner = Window.GetWindow(this),
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
                    System.Windows.MessageBox.Show(Window.GetWindow(this), "A profile with this name already exists.", "Profile exists", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}
