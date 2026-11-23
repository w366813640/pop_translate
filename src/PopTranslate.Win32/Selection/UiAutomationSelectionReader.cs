using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using PopTranslate.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace PopTranslate.Win32.Selection;

public class UiAutomationSelectionReader
{
    private const int DefaultTimeoutMs = 150;

    public virtual async Task<string?> TryReadSelectionTextAsync(
        nint? targetWindow = null,
        ScreenPoint? probePoint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var workTask = Task.Run(() => ReadSelectionSynchronously(targetWindow, probePoint), CancellationToken.None);

        try
        {
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
    }

    private static string? ReadSelectionSynchronously(nint? targetWindow, ScreenPoint? probePoint)
    {
        IUIAutomation? automation = null;
        try
        {
            automation = CreateAutomationClient();
            if (automation is null)
            {
                return null;
            }

            // 1. Focused element is the most reliable and zero-invasive source.
            var focusedText = TryReadFromFocusedElement(automation);
            if (!string.IsNullOrWhiteSpace(focusedText))
            {
                return focusedText;
            }

            // 2. Fallback to element directly under the mouse/probe coordinate.
            if (probePoint.HasValue)
            {
                var pointText = TryReadFromPoint(automation, probePoint.Value);
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
        finally
        {
            ReleaseComObjectSafe(automation);
        }
    }

    private static IUIAutomation? CreateAutomationClient()
    {
        try
        {
            return (IUIAutomation)new CUIAutomation8();
        }
        catch
        {
            try
            {
                return (IUIAutomation)new CUIAutomation();
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? TryReadFromFocusedElement(IUIAutomation automation)
    {
        IUIAutomationElement? element = null;
        try
        {
            element = automation.GetFocusedElement();
            return element is null ? null : TryExtractSelectedText(element);
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

    private static string? TryReadFromPoint(IUIAutomation automation, ScreenPoint point)
    {
        IUIAutomationElement? element = null;
        try
        {
            element = automation.ElementFromPoint(new Point(point.X, point.Y));
            return element is null ? null : TryExtractSelectedText(element);
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
            return element is null ? null : TryExtractSelectedText(element);
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

    private static string? TryExtractSelectedText(IUIAutomationElement element)
    {
        var text = TryExtractViaTextPattern(element, UIA_PATTERN_ID.UIA_TextPattern2Id);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return TryExtractViaTextPattern(element, UIA_PATTERN_ID.UIA_TextPatternId);
    }

    private static string? TryExtractViaTextPattern(IUIAutomationElement element, UIA_PATTERN_ID patternId)
    {
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
                rangeArray = textPattern2.GetSelection();
            }
            else if (patternObj is IUIAutomationTextPattern textPattern)
            {
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
