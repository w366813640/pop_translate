using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PopTranslate.Win32.Selection;

public class UiAutomationSelectionReader
{
    private const int DefaultTimeoutMs = 150;
    private const int MaxAncestorWalkDepth = 5;
    private const int MaxReadOnlyValueLength = 4096;

    private static readonly Lazy<IUIAutomation?> SharedAutomation = new(
        CreateConfiguredAutomationClient,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SemaphoreSlim _concurrencyGate = new(2, 2);

    public virtual async Task<string?> TryReadSelectionTextAsync(
        nint? targetWindow = null,
        ScreenPoint? probePoint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var workTask = Task.Run(() => ReadSelectionSynchronously(targetWindow, probePoint), CancellationToken.None);
            return await workTask.WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private static string? ReadSelectionSynchronously(nint? targetWindow, ScreenPoint? probePoint)
    {
        try
        {
            var automation = SharedAutomation.Value;
            if (automation is null)
            {
                return null;
            }

            // 1. Focused element (verified against targetWindow when provided) is the most reliable source.
            var focusedText = TryReadFromFocusedElement(automation, targetWindow);
            if (!string.IsNullOrWhiteSpace(focusedText))
            {
                return focusedText;
            }

            // 2. Fallback to element directly under the mouse/probe coordinate (walking ancestors for TextPattern).
            if (probePoint.HasValue)
            {
                var pointText = TryReadFromPoint(automation, probePoint.Value, targetWindow);
                if (!string.IsNullOrWhiteSpace(pointText))
                {
                    return pointText;
                }
            }

            // 3. Fallback to target HWND root element (e.g., rich-edit or custom host).
            if (targetWindow is > 0)
            {
                var windowText = TryReadFromWindow(automation, targetWindow.Value);
                if (!string.IsNullOrWhiteSpace(windowText))
                {
                    return windowText;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IUIAutomation? CreateConfiguredAutomationClient()
    {
        IUIAutomation? client = null;
        try
        {
            client = (IUIAutomation)new CUIAutomation8();
        }
        catch
        {
            try
            {
                client = (IUIAutomation)new CUIAutomation();
            }
            catch
            {
                return null;
            }
        }

        if (client is IUIAutomation2 automation2)
        {
            try
            {
                automation2.ConnectionTimeout = 100;
                automation2.TransactionTimeout = 150;
            }
            catch
            {
                // Ignore if platform rejects timeout tuning.
            }
        }

        return client;
    }

    private static string? TryReadFromFocusedElement(IUIAutomation automation, nint? targetWindow)
    {
        IUIAutomationElement? element = null;
        try
        {
            element = automation.GetFocusedElement();
            if (element is null || !DoesElementBelongToTargetWindow(element, targetWindow))
            {
                return null;
            }

            return TryExtractSelectedTextWithAncestors(automation, element, targetWindow, allowReadOnlyValueFallback: true);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(element);
        }
    }

    private static string? TryReadFromPoint(IUIAutomation automation, ScreenPoint point, nint? targetWindow)
    {
        IUIAutomationElement? element = null;
        try
        {
            element = automation.ElementFromPoint(new Point(point.X, point.Y));
            if (element is null || !DoesElementBelongToTargetWindow(element, targetWindow))
            {
                return null;
            }

            return TryExtractSelectedTextWithAncestors(automation, element, targetWindow, allowReadOnlyValueFallback: false);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(element);
        }
    }

    private static string? TryReadFromWindow(IUIAutomation automation, nint windowHandle)
    {
        IUIAutomationElement? element = null;
        try
        {
            var hwnd = new HWND(windowHandle);
            if (!PInvoke.IsWindow(hwnd))
            {
                return null;
            }

            element = automation.ElementFromHandle(hwnd);
            return element is null ? null : TryExtractFromSingleElement(element, allowReadOnlyValueFallback: false);
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(element);
        }
    }

    private static string? TryExtractSelectedTextWithAncestors(
        IUIAutomation automation,
        IUIAutomationElement startElement,
        nint? targetWindow,
        bool allowReadOnlyValueFallback)
    {
        var direct = TryExtractFromSingleElement(startElement, allowReadOnlyValueFallback);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? current = null;

        try
        {
            walker = automation.ControlViewWalker;
            if (walker is null)
            {
                return null;
            }

            current = startElement;
            for (var depth = 0; depth < MaxAncestorWalkDepth; depth++)
            {
                IUIAutomationElement? parent = null;
                try
                {
                    parent = walker.GetParentElement(current);
                }
                catch
                {
                    parent = null;
                }

                if (!ReferenceEquals(current, startElement))
                {
                    ReleaseComObjectSafe(current);
                }

                current = null;

                if (parent is null)
                {
                    break;
                }

                if (!DoesElementBelongToTargetWindow(parent, targetWindow))
                {
                    ReleaseComObjectSafe(parent);
                    break;
                }

                var ancestorSelection = TryExtractViaTextPatterns(parent, out _);
                if (!string.IsNullOrWhiteSpace(ancestorSelection))
                {
                    ReleaseComObjectSafe(parent);
                    return ancestorSelection;
                }

                current = parent;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (current is not null && !ReferenceEquals(current, startElement))
            {
                ReleaseComObjectSafe(current);
            }

            ReleaseComObjectSafe(walker);
        }
    }

    private static string? TryExtractFromSingleElement(IUIAutomationElement element, bool allowReadOnlyValueFallback)
    {
        if (IsPasswordElement(element))
        {
            return null;
        }

        var text = TryExtractViaTextPatterns(element, out var hasTextPattern);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // If the element implements TextPattern/TextPattern2, GetSelection() is authoritative.
        if (hasTextPattern || !allowReadOnlyValueFallback)
        {
            return null;
        }

        return TryExtractReadOnlyFocusedValue(element);
    }

    private static string? TryExtractViaTextPatterns(IUIAutomationElement element, out bool supportedAnyTextPattern)
    {
        var text2 = TryExtractViaTextPattern(element, UIA_PATTERN_ID.UIA_TextPattern2Id, out var hasPattern2);
        if (!string.IsNullOrWhiteSpace(text2))
        {
            supportedAnyTextPattern = true;
            return text2;
        }

        var text1 = TryExtractViaTextPattern(element, UIA_PATTERN_ID.UIA_TextPatternId, out var hasPattern1);
        supportedAnyTextPattern = hasPattern2 || hasPattern1;
        return text1;
    }

    private static string? TryExtractViaTextPattern(
        IUIAutomationElement element,
        UIA_PATTERN_ID patternId,
        out bool patternSupported)
    {
        patternSupported = false;
        object? patternObj = null;
        IUIAutomationTextRangeArray? rangeArray = null;

        try
        {
            patternObj = element.GetCurrentPattern(patternId);
            if (patternObj is null)
            {
                return null;
            }

            if (patternObj is IUIAutomationTextPattern2 textPattern2)
            {
                patternSupported = true;
                rangeArray = textPattern2.GetSelection();
            }
            else if (patternObj is IUIAutomationTextPattern textPattern)
            {
                patternSupported = true;
                rangeArray = textPattern.GetSelection();
            }

            if (rangeArray is null)
            {
                return null;
            }

            var count = rangeArray.Length;
            if (count <= 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                IUIAutomationTextRange? range = null;
                try
                {
                    range = rangeArray.GetElement(i);
                    if (range is null)
                    {
                        continue;
                    }

                    var segment = ReadRangeText(range);
                    if (!string.IsNullOrEmpty(segment))
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(segment);
                    }
                }
                finally
                {
                    ReleaseComObjectSafe(range);
                }
            }

            var result = builder.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(rangeArray);
            ReleaseComObjectSafe(patternObj);
        }
    }

    private static unsafe string? TryExtractReadOnlyFocusedValue(IUIAutomationElement element)
    {
        object? patternObj = null;
        try
        {
            var controlType = element.CurrentControlType;
            if (controlType is not UIA_CONTROLTYPE_ID.UIA_EditControlTypeId
                and not UIA_CONTROLTYPE_ID.UIA_TextControlTypeId)
            {
                return null;
            }

            if (!element.CurrentHasKeyboardFocus)
            {
                return null;
            }

            patternObj = element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId);
            if (patternObj is not IUIAutomationValuePattern valuePattern)
            {
                return null;
            }

            if (!valuePattern.CurrentIsReadOnly)
            {
                return null;
            }

            var bstr = valuePattern.CurrentValue;
            if (bstr.Value is null)
            {
                return null;
            }

            try
            {
                var value = bstr.ToString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > MaxReadOnlyValueLength)
                {
                    return null;
                }

                return value;
            }
            finally
            {
                PInvoke.SysFreeString(bstr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObjectSafe(patternObj);
        }
    }

    private static bool IsPasswordElement(IUIAutomationElement element)
    {
        try
        {
            return element.CurrentIsPassword;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe bool DoesElementBelongToTargetWindow(IUIAutomationElement element, nint? targetWindow)
    {
        if (targetWindow is null || targetWindow.Value <= 0)
        {
            return true;
        }

        var targetHwnd = new HWND(targetWindow.Value);
        if (!PInvoke.IsWindow(targetHwnd))
        {
            return false;
        }

        var targetRoot = PInvoke.GetAncestor(targetHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (targetRoot == HWND.Null)
        {
            targetRoot = targetHwnd;
        }

        HWND elemHwnd = HWND.Null;
        try
        {
            elemHwnd = element.CurrentNativeWindowHandle;
        }
        catch
        {
            // Many virtualized DOM/WPF/WinUI elements do not expose a native HWND.
        }

        if (elemHwnd != HWND.Null)
        {
            if (elemHwnd == targetHwnd ||
                elemHwnd == targetRoot ||
                PInvoke.IsChild(targetRoot, elemHwnd))
            {
                return true;
            }

            var elemRoot = PInvoke.GetAncestor(elemHwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
            return elemRoot != HWND.Null && elemRoot == targetRoot;
        }

        int elemPid;
        try
        {
            elemPid = element.CurrentProcessId;
        }
        catch
        {
            return false;
        }

        if (elemPid <= 0)
        {
            return false;
        }

        uint targetPid = 0;
        PInvoke.GetWindowThreadProcessId(targetRoot, &targetPid);
        if (targetPid != 0 && (uint)elemPid == targetPid)
        {
            return true;
        }

        // Support UWP ApplicationFrameHost where child CoreWindow runs in a separate app process.
        var matchedChildPid = false;
        WNDENUMPROC enumProc = (childHwnd, _) =>
        {
            uint childPid = 0;
            PInvoke.GetWindowThreadProcessId(childHwnd, &childPid);
            if (childPid == (uint)elemPid)
            {
                matchedChildPid = true;
                return false;
            }

            return true;
        };

        PInvoke.EnumChildWindows(targetRoot, enumProc, new LPARAM(0));
        return matchedChildPid;
    }

    private static unsafe string? ReadRangeText(IUIAutomationTextRange range)
    {
        var bstr = range.GetText(-1);
        if (bstr.Value is null)
        {
            return null;
        }

        try
        {
            return bstr.ToString();
        }
        finally
        {
            PInvoke.SysFreeString(bstr);
        }
    }

    private static void ReleaseComObjectSafe(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try
            {
                Marshal.ReleaseComObject(comObj);
            }
            catch
            {
                // Best-effort COM release.
            }
        }
    }
}
