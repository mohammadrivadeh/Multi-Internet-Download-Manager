using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using DownloadManager.App.Presentation;

namespace DownloadManager.App.Views.Shell;

internal static class EditorDialogs
{
    internal sealed record Field(string Label, string Value, Func<string?>? Browse = null, IReadOnlyList<string>? Options = null, bool AllowCustomValue = false);
    public static Window Create(Window owner, string title, object content, double width, bool hasOwnHeader = false)
    {
        var window = new AnimatedEditorWindow
        {
            Owner = owner, Title = title, Content = content, Width = width,
            SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height * .85,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
        };
        window.SetResourceReference(Control.BackgroundProperty, "App.Background");
        window.SetResourceReference(Control.ForegroundProperty, "App.Text");
        window.SetResourceReference(Control.FontFamilyProperty, "App.FontFamily");
        if (!hasOwnHeader)
        {
            var titleBar = new DockPanel { Margin = new Thickness(16, 6, 8, 6) };
            var icon = new TextBlock { Text = "\uE8BB" };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "App.IconFont");
            var close = new Button { Content = icon, Width = 32, Height = 32, MinHeight = 32, Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = "Close" };
            System.Windows.Automation.AutomationProperties.SetName(close, "Close");
            close.Click += (_, _) => window.Close();
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            heading.MouseLeftButtonDown += (_, args) =>
            {
                if (args.LeftButton == System.Windows.Input.MouseButtonState.Pressed) window.DragMove();
            };
            titleBar.Children.Add(heading);
            var header = new Border { Child = titleBar, BorderThickness = new Thickness(0, 0, 0, 1) };
            header.SetResourceReference(Border.BackgroundProperty, "App.SurfaceAlt");
            header.SetResourceReference(Border.BorderBrushProperty, "App.BorderSubtle");
            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(new ContentPresenter { Content = content });
            var frame = new Border { Child = layout, BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "App.Border");
            window.Content = frame;
        }
        return window;
    }

    private sealed class AnimatedEditorWindow : Window
    {
        private bool dismissing;
        private bool dismissed;

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (Content is UIElement surface) AppMotion.Enter(surface);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (dismissing && !dismissed)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
            if (e.Cancel || dismissed || !AppMotion.Enabled || !IsVisible || Content is not UIElement surface)
                return;

            var result = DialogResult;
            e.Cancel = true;
            dismissing = true;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                await AppMotion.DismissAsync(surface);
                dismissed = true;
                if (!IsVisible) return;
                if (result.HasValue && DialogResult != result) DialogResult = result;
                else Close();
            });
        }
    }

    public static Task<bool> EditTextAsync(Window owner, string title, string label, string initial,
        Func<string, Task<string?>> submit) =>
        EditFieldsAsync(owner, title, [new(label, initial)], values => submit(values[0]));

    public static Task<bool> EditFieldsAsync(Window owner, string title, IReadOnlyList<Field> fields,
        Func<string[], Task<string?>> submit, IReadOnlyList<(string Label, Action Select)>? actions = null)
    {
        var inputs = fields.Select<Field, Control>(field => field.Options is { } options
            ? new ComboBox { ItemsSource = options, IsEditable = field.AllowCustomValue, SelectedItem = field.Value, Text = field.Value }
            : new TextBox { Text = field.Value }).ToArray();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(4, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(18) };
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var input = inputs[index];
            input.Height = 40;
            input.Padding = new Thickness(12, 6, 12, 6);
            input.VerticalContentAlignment = VerticalAlignment.Center;
            input.Margin = new Thickness(0, 4, 0, 8);
            content.Children.Add(new Label { Content = field.Label, Target = input });
            if (field.Browse is { } browse && input is TextBox pathInput)
            {
                var row = new DockPanel();
                var folderIcon = new TextBlock { Text = "\uE838" };
                folderIcon.SetResourceReference(TextBlock.FontFamilyProperty, "App.IconFont");
                var folder = new Button { Content = folderIcon, Width = 40, Height = 40, MinHeight = 40, Padding = new Thickness(0), ToolTip = "Browse for a folder",
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 4, 0, 8) };
                System.Windows.Automation.AutomationProperties.SetName(folder, "Browse for a folder");
                DockPanel.SetDock(folder, Dock.Right);
                folder.Click += (_, _) => { if (browse() is { } chosen) pathInput.Text = chosen; };
                row.Children.Add(folder);
                row.Children.Add(input);
                content.Children.Add(row);
            }
            else content.Children.Add(input);
        }
        content.Children.Add(error);
        content.Children.Add(buttons);
        var window = Create(owner, title, new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 420);
        var saving = false;
        window.Closing += (_, args) => args.Cancel = saving;
        window.Loaded += (_, _) =>
        {
            inputs[0].Focus();
            if (inputs[0] is TextBox input) input.SelectAll();
        };
        if (actions is not null)
            foreach (var (label, select) in actions)
            {
                var action = new Button { Content = label };
                action.Click += (_, _) =>
                {
                    if (saving) return;
                    select();
                    window.DialogResult = false;
                };
                buttons.Children.Insert(0, action);
            }
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            cancel.IsEnabled = false;
            saving = true;
            try
            {
                var message = await submit(inputs.Select(input => input is TextBox box ? box.Text : ((ComboBox)input).Text).ToArray());
                saving = false;
                if (!window.IsVisible) return;
                if (message is null) window.DialogResult = true;
                else error.Text = message;
            }
            finally { saving = false; save.IsEnabled = true; cancel.IsEnabled = true; }
        };
        return Task.FromResult(window.ShowDialog() == true);
    }
}
