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
    private static readonly FontFamily CodeFontFamily = new("Cascadia Mono, Consolas, monospace");

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ServiceCardViewModel),
            typeof(TranslationResultCardControl),
            new PropertyMetadata(null, OnViewModelChanged));

    private ServiceCardViewModel? _subscribedViewModel;
    private string? _lastRenderedMarkdownText;
    private ElementTheme _lastRenderedMarkdownTheme = ElementTheme.Default;

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
        _lastRenderedMarkdownText = null;
        RenderFormattedMarkdown();
        RenderDiff();
    }

    private void AttachViewModel(ServiceCardViewModel? next)
    {
        if (ReferenceEquals(_subscribedViewModel, next))
        {
            RenderFormattedMarkdown();
            RenderDiff();
            return;
        }

        DetachViewModel();

        if (next is not null)
        {
            _subscribedViewModel = next;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedViewModel.DiffSegmentsChanged += OnViewModelDiffSegmentsChanged;
        }

        _lastRenderedMarkdownText = null;
        RenderFormattedMarkdown();
        RenderDiff();
    }

    private void DetachViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.DiffSegmentsChanged -= OnViewModelDiffSegmentsChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelDiffSegmentsChanged(object? sender, EventArgs e)
    {
        RenderDiff();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServiceCardViewModel.ShowDiffText)
            or nameof(ServiceCardViewModel.DiffSegments))
        {
            RenderDiff();
        }

        if (e.PropertyName is nameof(ServiceCardViewModel.ShowFormattedMarkdown)
            or nameof(ServiceCardViewModel.TranslatedText)
            or nameof(ServiceCardViewModel.State))
        {
            RenderFormattedMarkdown();
        }
    }

    private void RenderFormattedMarkdown()
    {
        var vm = ViewModel;
        if (vm is null || !vm.ShowFormattedMarkdown || string.IsNullOrEmpty(vm.TranslatedText))
        {
            if (FormattedRichTextBlock.Blocks.Count > 0)
            {
                FormattedRichTextBlock.Blocks.Clear();
                FormattedRichTextBlock.TextHighlighters.Clear();
            }

            _lastRenderedMarkdownText = null;
            return;
        }

        var currentTheme = ActualTheme;
        if (string.Equals(_lastRenderedMarkdownText, vm.TranslatedText, StringComparison.Ordinal) &&
            _lastRenderedMarkdownTheme == currentTheme &&
            FormattedRichTextBlock.Blocks.Count > 0)
        {
            return;
        }

        _lastRenderedMarkdownText = vm.TranslatedText;
        _lastRenderedMarkdownTheme = currentTheme;

        FormattedRichTextBlock.Blocks.Clear();
        FormattedRichTextBlock.TextHighlighters.Clear();

        var isDark = currentTheme == ElementTheme.Dark;
        var codeFgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE)
            : Color.FromArgb(0xFF, 0x0F, 0x4C, 0x81));
        var codeBgBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x18, 0x00, 0x00, 0x00));

        var codeHighlighter = new TextHighlighter
        {
            Background = codeBgBrush,
            Foreground = codeFgBrush,
        };

        var normalized = vm.TranslatedText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var paragraph = new Paragraph();
        var charOffset = 0;

        BuildMarkdownInlines(normalized, paragraph, codeHighlighter, codeFgBrush, ref charOffset);

        FormattedRichTextBlock.Blocks.Add(paragraph);
        if (codeHighlighter.Ranges.Count > 0)
        {
            FormattedRichTextBlock.TextHighlighters.Add(codeHighlighter);
        }
    }

    private static void BuildMarkdownInlines(
        string text,
        Paragraph paragraph,
        TextHighlighter codeHighlighter,
        SolidColorBrush codeFgBrush,
        ref int charOffset)
    {
        var index = 0;
        var length = text.Length;

        while (index < length)
        {
            // 1. Check for fenced code block ```lang\n...\n```
            if ((index == 0 || text[index - 1] == '\n') &&
                index + 3 <= length &&
                text.AsSpan(index, 3).SequenceEqual("```"))
            {
                var fenceHeaderEnd = text.IndexOf('\n', index + 3);
                if (fenceHeaderEnd >= 0)
                {
                    var closingFence = text.IndexOf("```", fenceHeaderEnd + 1, StringComparison.Ordinal);
                    if (closingFence >= 0)
                    {
                        var codeBody = text.Substring(fenceHeaderEnd + 1, closingFence - (fenceHeaderEnd + 1));
                        if (codeBody.EndsWith('\n'))
                        {
                            codeBody = codeBody[..^1];
                        }

                        if (codeBody.Length > 0)
                        {
                            AppendCodeRun(paragraph, codeBody, codeHighlighter, codeFgBrush, ref charOffset);
                        }

                        var afterClose = closingFence + 3;
                        while (afterClose < length && (text[afterClose] == ' ' || text[afterClose] == '\t'))
                        {
                            afterClose++;
                        }

                        index = afterClose;
                        continue;
                    }
                }
            }

            // 2. Check for inline `code` (single-line)
            if (text[index] == '`')
            {
                var closeTick = text.IndexOf('`', index + 1);
                var nextNewline = text.IndexOf('\n', index + 1);
                if (closeTick > index + 1 && (nextNewline < 0 || closeTick < nextNewline))
                {
                    var inlineCode = text.Substring(index + 1, closeTick - index - 1);
                    AppendCodeRun(paragraph, inlineCode, codeHighlighter, codeFgBrush, ref charOffset);
                    index = closeTick + 1;
                    continue;
                }
            }

            // 3. Check for **bold**
            if (index + 4 <= length && text[index] == '*' && text[index + 1] == '*')
            {
                var closeBold = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                var nextNewline = text.IndexOf('\n', index + 2);
                if (closeBold > index + 2 && (nextNewline < 0 || closeBold < nextNewline))
                {
                    var boldContent = text.Substring(index + 2, closeBold - index - 2);
                    paragraph.Inlines.Add(new Run
                    {
                        Text = boldContent,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                    charOffset += boldContent.Length;
                    index = closeBold + 2;
                    continue;
                }
            }

            // 4. Plain text run until next potential markdown token
            var start = index;
            index++;
            while (index < length)
            {
                var ch = text[index];
                if (ch == '`')
                {
                    break;
                }

                if (ch == '*' && index + 1 < length && text[index + 1] == '*')
                {
                    break;
                }

                index++;
            }

            var plainSegment = text.Substring(start, index - start);
            paragraph.Inlines.Add(new Run { Text = plainSegment });
            charOffset += plainSegment.Length;
        }
    }

    private static void AppendCodeRun(
        Paragraph paragraph,
        string codeText,
        TextHighlighter codeHighlighter,
        SolidColorBrush codeFgBrush,
        ref int charOffset)
    {
        if (codeText.Length == 0)
        {
            return;
        }

        paragraph.Inlines.Add(new Run
        {
            Text = codeText,
            FontFamily = CodeFontFamily,
            Foreground = codeFgBrush,
        });
        codeHighlighter.Ranges.Add(new TextRange
        {
            StartIndex = charOffset,
            Length = codeText.Length,
        });
        charOffset += codeText.Length;
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
