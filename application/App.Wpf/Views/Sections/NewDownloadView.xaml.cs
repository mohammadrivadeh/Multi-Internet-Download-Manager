using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using DownloadManager.App.ViewModels;
namespace DownloadManager.App.Views.Sections;
public partial class NewDownloadView : UserControl
{
    public NewDownloadView() => InitializeComponent();

    private void FocusUrl(object sender, RoutedEventArgs e) => UrlInput.Focus();

    private async void PasteUrl(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText() && DataContext is MainViewModel model)
            {
                model.Url = Clipboard.GetText().Trim();
                UrlInput.Focus();
                UrlInput.CaretIndex = UrlInput.Text.Length;
                await model.PreviewFilenameAsync();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            if (DataContext is MainViewModel model) model.NewDownloadError = "Clipboard is unavailable. Try again.";
        }
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            Window.GetWindow(this)?.DragMove();
    }

    private async void SubmitClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || model.AddCommand.IsRunning) return;
        SubmitButton.IsEnabled = false;
        try
        {
            await model.AddCommand.ExecuteAsync(null);
            if (model.DownloadAdded && Window.GetWindow(this) is { IsVisible: true } dialog)
                dialog.DialogResult = true;
        }
        finally { SubmitButton.IsEnabled = true; }
    }
}
