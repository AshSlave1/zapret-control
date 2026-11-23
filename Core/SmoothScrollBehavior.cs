using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace ZapretControl.Core;

/// <summary>
/// Плавная прокрутка колесом для страниц приложения. Обрабатывает
/// PreviewMouseWheel на уровне страницы, поэтому колесо работает и над
/// вложенными ListBox/ItemsControl, которые иначе «съедают» событие.
/// Вложенные прокручиваемые поля (например, редакторы списков) сохраняют
/// собственную прокрутку, пока им есть куда двигаться.
/// </summary>
public static class SmoothScrollBehavior
{
    private const double PixelsPerNotch = 132;
    private const double DurationMs = 190;
    private const double TickMs = 15;

    private sealed class ScrollState
    {
        public ScrollViewer Viewer = null!;
        public DispatcherTimer Timer = null!;
        public Stopwatch Clock = new();
        public double Start;
        public double Target;
    }

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;

        if ((bool)e.NewValue)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.PreviewMouseDown += OnPreviewMouseDown;
            viewer.PreviewKeyDown += OnPreviewKeyDown;
            viewer.Unloaded += OnUnloaded;
        }
        else
        {
            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            viewer.PreviewMouseDown -= OnPreviewMouseDown;
            viewer.PreviewKeyDown -= OnPreviewKeyDown;
            viewer.Unloaded -= OnUnloaded;
            Stop(viewer);
            States.Remove(viewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Delta == 0)
            return;

        if (viewer.ScrollableHeight <= 0.5)
            return;

        if (NestedCanScroll(e.OriginalSource as DependencyObject, viewer, e.Delta))
            return;

        var state = GetState(viewer);
        var current = viewer.VerticalOffset;
        var delta = -e.Delta / 120.0 * PixelsPerNotch;

        if (!state.Timer.IsEnabled)
            state.Target = current;

        state.Target = Math.Clamp(state.Target + delta, 0, viewer.ScrollableHeight);
        state.Start = current;
        state.Clock.Restart();

        if (!state.Timer.IsEnabled)
            state.Timer.Start();

        e.Handled = true;
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer || timer.Tag is not ScrollState state)
            return;

        var progress = Math.Min(1, state.Clock.Elapsed.TotalMilliseconds / DurationMs);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var value = state.Start + (state.Target - state.Start) * eased;

        state.Viewer.ScrollToVerticalOffset(value);

        if (progress >= 1)
        {
            state.Viewer.ScrollToVerticalOffset(state.Target);
            timer.Stop();
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => StopIfViewer(sender);

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        => StopIfViewer(sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Вкладки TabControl выгружают содержимое при переключении, но сам
        // ScrollViewer переиспользуется. Останавливаем анимацию, но не
        // отписываемся: иначе после возврата на вкладку колесо перестанет
        // работать, потому что attached-свойство уже равно true.
        if (sender is ScrollViewer viewer)
            Stop(viewer);
    }

    private static void StopIfViewer(object sender)
    {
        if (sender is ScrollViewer viewer)
            Stop(viewer);
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state))
            state.Timer.Stop();
    }

    private static ScrollState GetState(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state))
            return state;

        state = new ScrollState { Viewer = viewer };
        state.Timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(TickMs),
            Tag = state
        };
        state.Timer.Tick += OnTick;
        States[viewer] = state;
        return state;
    }

    private static bool NestedCanScroll(DependencyObject? source, ScrollViewer page, int delta)
    {
        var current = source;

        while (current is not null && !ReferenceEquals(current, page))
        {
            switch (current)
            {
                case TextBox textBox:
                    if (CanScroll(FindTextBoxScrollViewer(textBox), delta))
                        return true;
                    break;

                case ScrollViewer nested:
                    if (CanScroll(nested, delta))
                        return true;
                    break;

                case ListBox listBox:
                    if (CanScroll(FindDescendantScrollViewer(listBox), delta))
                        return true;
                    break;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static bool CanScroll(ScrollViewer? viewer, int delta)
    {
        if (viewer is null || viewer.ScrollableHeight <= 0.5)
            return false;

        return delta < 0
            ? viewer.VerticalOffset > 0.5
            : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;
    }

    private static ScrollViewer? FindTextBoxScrollViewer(TextBox textBox)
    {
        if (textBox.Template?.FindName("PART_ContentHost", textBox) is ScrollViewer host)
            return host;

        return FindDescendantScrollViewer(textBox);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject node)
    {
        if (node is Visual or Visual3D)
            return VisualTreeHelper.GetParent(node);

        return LogicalTreeHelper.GetParent(node);
    }
}
