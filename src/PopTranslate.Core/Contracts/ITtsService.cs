using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface ITtsService : IDisposable
{
    bool IsSpeaking { get; }

    Task SpeakAsync(string text, LanguageCode? language = null, CancellationToken cancellationToken = default);

    void Stop();
}
