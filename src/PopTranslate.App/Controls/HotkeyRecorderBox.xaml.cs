using PopTranslate.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace PopTranslate.App.Controls;

public sealed partial class HotkeyRecorderBox : UserControl
{
    public static readonly DependencyProperty GestureProperty =
        DependencyProperty.Register(
            nameof(Gesture),
            typeof(HotkeyGesture),
            typeof(HotkeyRecorderBox),
            new PropertyMetadata(HotkeyGesture.Empty, OnVisualStatePropertyChanged));

    public static readonly DependencyProperty ErrorMessageProperty =
        DependencyProperty.Register(
            nameof(ErrorMessage),
            typeof(string),
            typeof(HotkeyRecorderBox),
            new PropertyMetadata(null, OnVisualStatePropertyChanged));

    private bool _isRecording;

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    public event EventHandler<HotkeyGesture>? GestureChanged;

    public HotkeyGesture Gesture
    {
        get => (HotkeyGesture?)GetValue(GestureProperty) ?? HotkeyGesture.Empty;
        set => SetValue(GestureProperty, value ?? HotkeyGesture.Empty);
    }

    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    private static void OnVisualStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorderBox box)
        {
            box.UpdateVisuals();
        }
    }

    private void RecorderButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = !_isRecording;
        UpdateVisuals();
        if (_isRecording)
        {
            RecorderButton.Focus(FocusState.Keyboard);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = false;
        CommitGesture(HotkeyGesture.Empty);
    }

    private void RecorderButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            _isRecording = false;
            UpdateVisuals();
        }
    }

    private void RecorderButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key;

        if (key == VirtualKey.Escape)
        {
            _isRecording = false;
            UpdateVisuals();
            return;
        }

        var modifiers = GetPressedModifiers();

        if ((key is VirtualKey.Back or VirtualKey.Delete) && modifiers == HotkeyModifiers.None)
        {
            _isRecording = false;
            CommitGesture(HotkeyGesture.Empty);
            return;
        }

        if (IsModifierKey(key))
        {
            DisplayLabel.Text = FormatRecordingHint(modifiers);
            return;
        }

        var vk = (uint)key;
        var isFunctionKey = vk is >= 0x70 and <= 0x87; // F1-F24
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            DisplayLabel.Text = "请配合 Ctrl / Alt / Shift / Win 使用";
            return;
        }

        _isRecording = false;
        CommitGesture(new HotkeyGesture(modifiers, vk));
    }

    private void CommitGesture(HotkeyGesture gesture)
    {
        Gesture = gesture;
        UpdateVisuals();
        GestureChanged?.Invoke(this, gesture);
    }

    private void UpdateVisuals()
    {
        if (_isRecording)
        {
            DisplayLabel.Text = "请按下组合键 (Esc 取消)...";
            RecorderButton.BorderBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
            StatusGlyph.Foreground = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush;
        }
        else
        {
            var gesture = Gesture;
            DisplayLabel.Text = gesture.IsEmpty ? "未设置 (点击录制)" : gesture.DisplayText;
            RecorderButton.ClearValue(BorderBrushProperty);
            StatusGlyph.ClearValue(IconElement.ForegroundProperty);
        }

        ClearButton.IsEnabled = !Gesture.IsEmpty;

        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            ErrorTextBlock.Text = ErrorMessage;
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorTextBlock.Text = string.Empty;
            ErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private static HotkeyModifiers GetPressedModifiers()
    {
        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl))
        {
            mods |= HotkeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftMenu) || IsDown(VirtualKey.RightMenu))
        {
            mods |= HotkeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift) || IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift))
        {
            mods |= HotkeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            mods |= HotkeyModifiers.Win;
        }

        return mods;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static string FormatRecordingHint(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None)
        {
            return "请按下组合键 (Esc 取消)...";
        }

        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add("...");
        return string.Join(" + ", parts);
    }
}
