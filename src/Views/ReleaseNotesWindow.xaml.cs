using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace blink_o_mat.Views;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(string version, string markdown)
    {
        InitializeComponent();

        HeaderText.Text = string.IsNullOrWhiteSpace(version)
            ? "What's new"
            : $"What's new in version {version}";

        NotesViewer.Markdown = markdown ?? string.Empty;

        // Open links in the default browser
        CommandManager.AddPreviewExecutedHandler(NotesViewer, OnHyperlinkExecuted);
    }

    private static void OnHyperlinkExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (e.Command == Markdig.Wpf.Commands.Hyperlink && e.Parameter is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // ignored
            }
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
