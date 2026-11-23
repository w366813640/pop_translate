using System.ComponentModel;
using PopTranslate.App.ViewModels;
using PopTranslate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace PopTranslate.App.Controls;

public sealed partial class TranslationResultCardControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ServiceCardViewModel),
            typeof(TranslationResultCardControl),
            new PropertyMetadata(null, OnViewModelChanged));

    private ServiceCardViewModel? _subscribedViewModel;

    public TranslationResultCardControl()
    {
        InitializeComponent();
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public ServiceCardViewModel? ViewModel
    {
        get => (ServiceCardViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TranslationResultCardControl control)
        {
            control.AttachViewModel(e.NewValue as ServiceCardViewModel);
        }
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(ViewModel);
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        RenderDiff();
    }

    private void AttachViewModel(ServiceCardViewModel? next)
    {
        if (ReferenceEquals(_subscribedViewModel, next))
        {
            RenderDiff();
            return;
        }

        DetachViewModel();

        if (next is not null)
        {
            _subscribedViewModel = next;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RenderDiff();
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServiceCardViewModel.ShowDiffText)
            or nameof(ServiceCardViewModel.DiffSegments))
        {
            RenderDiff();
        }
    }

    private void RenderDiff()
    {
        DiffRichTextBlock.Blocks.Clear();
        DiffRichTextBlock.TextHighlighters.Clear();

        var vm = ViewModel;
        if (vm is null || !vm.ShowDiffText || vm.DiffSegments.Count == 0)
        {
            return;
        }

        var isDark = ActualTheme == ElementTheme.Dark;

        var deleteFgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0xFD, 0xA2, 0x9B)
            : Color.FromArgb(0xFF, 0xB4, 0x23, 0x18));
        var deleteBgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x47, 0xF0, 0x44, 0x38)
            : Color.FromArgb(0x33, 0xF0, 0x44, 0x38));

        var insertFgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x6C, 0xE9, 0xA6)
            : Color.FromArgb(0xFF, 0x06, 0x76, 0x47));
        var insertBgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x40, 0x12, 0xB7, 0x6A)
            : Color.FromArgb(0x33, 0x12, 0xB7, 0x6A));

        var deleteHighlighter = new TextHighlighter
        {
            Background = deleteBgBrush,
            Foreground = deleteFgBrush,
        };
        var insertHighlighter = new TextHighlighter
        {
            Background = insertBgBrush,
            Foreground = insertFgBrush,
        };

        var paragraph = new Paragraph();
        var charOffset = 0;

        foreach (var segment in vm.DiffSegments)
        {
            if (string.IsNullOrEmpty(segment.Text))
            {
                continue;
            }

            // Normalize CRLF -> LF so RichTextBlock layout offsets stay aligned 1:1 with TextHighlighter TextRange.
            var displayText = segment.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            if (displayText.Length == 0)
            {
                continue;
            }

            var run = new Run { Text = displayText };

            switch (segment.Operation)
            {
                case DiffOperation.Delete:
                    run.TextDecorations = TextDecorations.Strikethrough;
                    run.Foreground = deleteFgBrush;
                    deleteHighlighter.Ranges.Add(new TextRange
                    {
                        StartIndex = charOffset,
                        Length = displayText.Length,
                    });
                    break;

                case DiffOperation.Insert:
                    run.Foreground = insertFgBrush;
                    insertHighlighter.Ranges.Add(new TextRange
                    {
                        StartIndex = charOffset,
                        Length = displayText.Length,
                    });
                    break;
            }

            paragraph.Inlines.Add(run);
            charOffset += displayText.Length;
        }

        DiffRichTextBlock.Blocks.Add(paragraph);

        if (deleteHighlighter.Ranges.Count > 0)
        {
            DiffRichTextBlock.TextHighlighters.Add(deleteHighlighter);
        }

        if (insertHighlighter.Ranges.Count > 0)
        {
            DiffRichTextBlock.TextHighlighters.Add(insertHighlighter);
        }
    }

    private void Header_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Ignore taps originating from action buttons or toggle buttons inside the header.
        if (e.OriginalSource is DependencyObject source && HasParentButton(source))
        {
            return;
        }

        if (ViewModel?.ToggleCollapseCommand.CanExecute(null) == true)
        {
            ViewModel.ToggleCollapseCommand.Execute(null);
        }
    }

    private static bool HasParentButton(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
