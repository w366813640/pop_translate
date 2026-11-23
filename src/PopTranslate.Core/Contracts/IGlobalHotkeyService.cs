using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    IReadOnlyDictionary<HotkeyAction, HotkeyRegistrationResult> ApplyHotkeys(HotkeyConfig config);

    void UnregisterAll();
}
