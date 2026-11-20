using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DownloadManager.App.Presentation;

internal static class AppMotion
{
    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "ScrollOffset", typeof(double), typeof(AppMotion), new PropertyMetadata(double.NaN,
            (sender, args) =>
            {
                if (args.NewValue is double offset && double.IsFinite(offset))
                    ((ScrollViewer)sender).ScrollToVerticalOffset(offset);
            }));
    private static readonly DependencyProperty ScrollTargetProperty = DependencyProperty.RegisterAttached(
        "ScrollTarget", typeof(double), typeof(AppMotion), new PropertyMetadata(double.NaN));

    private static int level = 2;
    public static int Level
    {
        get => level;
        set
        {
            level = value;
            UpdatePopupAnimation();
        }
    }
    public static bool Enabled => Level > 0 && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static double Intensity => Level switch { 1 => .65, 3 => 1.4, _ => 1 };

    public static void Register()
    {
        UpdatePopupAnimation();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation)
                or nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ComboBoxPopupAnimation))
                UpdatePopupAnimation();
        };
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent,
            new MouseEventHandler((sender, _) => { if (((UIElement)sender).IsEnabled) Reveal((UIElement)sender, .88, 110); }));
        EventManager.RegisterClassHandler(typeof(MenuItem), UIElement.MouseEnterEvent,
            new MouseEventHandler((sender, _) => { if (((UIElement)sender).IsEnabled) Reveal((UIElement)sender, .88, 110); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) => Reveal((UIElement)sender, .72, 150)));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(Scroll));
    }

    private static void UpdatePopupAnimation() =>
        Application.Current.Resources["App.ComboPopupAnimation"] = Enabled
            ? SystemParameters.ComboBoxPopupAnimation : PopupAnimation.None;

    public static void Reveal(UIElement element, double initialOpacity = .32, int milliseconds = 260)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            return;
        }
        var opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        element.BeginAnimation(UIElement.OpacityProperty,
            Ease(opacity * (1 - (1 - initialOpacity) * Intensity), opacity, milliseconds, FillBehavior.Stop));
    }

    public static void Enter(UIElement surface)
    {
        Reveal(surface);
        if (!Enabled) return;

        if (surface.RenderTransform == Transform.Identity)
            surface.RenderTransform = new TranslateTransform();
        if (surface.RenderTransform is TranslateTransform offset && !offset.IsFrozen)
            offset.BeginAnimation(TranslateTransform.YProperty, Ease(14 * Intensity, 0, 260, FillBehavior.Stop));
    }

    public static Task DismissAsync(UIElement element)
    {
        if (!Enabled || !element.IsVisible) return Task.CompletedTask;

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = Ease(element.Opacity, .1, 125, FillBehavior.HoldEnd);
        fade.Completed += (_, _) => completed.TrySetResult();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        return Task.WhenAny(completed.Task, Task.Delay(300));
    }

    private static DoubleAnimation Ease(double from, double to, int milliseconds, FillBehavior fill) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds * Intensity), fill)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

    private static void Scroll(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || !Enabled || sender is not ScrollViewer viewer
            || (viewer.CanContentScroll && (viewer.TemplatedParent is not ItemsControl items
                || VirtualizingPanel.GetScrollUnit(items) != ScrollUnit.Pixel))
            || viewer.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0
            || args.OriginalSource is not DependencyObject source || FindScrollViewer(source) != viewer)
            return;

        var step = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        var start = viewer.VerticalOffset;
        var pendingTarget = (double)viewer.GetValue(ScrollTargetProperty);
        if (double.IsNaN(pendingTarget)) pendingTarget = start;
        var target = Math.Clamp(pendingTarget - args.Delta / (double)Mouse.MouseWheelDeltaForOneLine * step, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - start) < .1 && target == pendingTarget) return;

        args.Handled = true;
        viewer.SetValue(ScrollTargetProperty, target);
        var animation = Ease(start, target, 140, FillBehavior.Stop);
        animation.Completed += (_, _) =>
        {
            viewer.ScrollToVerticalOffset(target);
            viewer.ClearValue(ScrollTargetProperty);
            viewer.BeginAnimation(ScrollOffsetProperty, null);
        };
        viewer.BeginAnimation(ScrollOffsetProperty, animation);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ScrollViewer viewer) return viewer;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
