using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace PopTranslate.Win32.Tts;

public sealed class WindowsAndEdgeTtsService : ITtsService
{
    private const int MaxSpeakCharacters = 8_000;

    private readonly ILanguageDetector? _languageDetector;
    private readonly object _sessionLock = new();

    private PlaybackSessionState? _activeSession;
    private bool _disposed;

    public WindowsAndEdgeTtsService(ILanguageDetector? languageDetector = null)
    {
        _languageDetector = languageDetector;
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_sessionLock)
            {
                return _activeSession is { IsCompleted: false };
            }
        }
    }

    public async Task SpeakAsync(
        string text,
        LanguageCode? language = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            Stop();
            return;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxSpeakCharacters)
        {
            trimmed = trimmed[..MaxSpeakCharacters];
        }

        PlaybackSessionState session;
        CancellationToken sessionToken;

        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();

            session = new PlaybackSessionState(cancellationToken);
            _activeSession = session;
            sessionToken = session.CombinedToken;
        }

        SpeechSynthesizer? synthesizer = null;
        SpeechSynthesisStream? synthesisStream = null;
        MediaSource? mediaSource = null;
        MediaPlayer? mediaPlayer = null;

        try
        {
            var effectiveLanguage = ResolveEffectiveLanguage(trimmed, language);

            synthesizer = new SpeechSynthesizer();
            var matchedVoice = ResolveBestVoice(effectiveLanguage);
            if (matchedVoice is not null)
            {
                synthesizer.Voice = matchedVoice;
            }

            synthesisStream = await synthesizer
                .SynthesizeTextToStreamAsync(trimmed)
                .AsTask(sessionToken)
                .ConfigureAwait(false);

            sessionToken.ThrowIfCancellationRequested();

            if (synthesisStream.Size == 0)
            {
                return;
            }

            mediaSource = MediaSource.CreateFromStream(synthesisStream, synthesisStream.ContentType);
            mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
            };

            var playbackFinishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            TypedEventHandler<MediaPlayer, object> onEnded = (_, _) => playbackFinishedTcs.TrySetResult();
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> onFailed = (_, _) => playbackFinishedTcs.TrySetResult();

            mediaPlayer.MediaEnded += onEnded;
            mediaPlayer.MediaFailed += onFailed;

            try
            {
                lock (_sessionLock)
                {
                    if (!ReferenceEquals(_activeSession, session) || session.IsStoppedByCaller || _disposed)
                    {
                        return;
                    }

                    session.AttachPlayer(mediaPlayer);
                }

                mediaPlayer.Source = mediaSource;
                mediaPlayer.Play();

                var maxPlaybackMs = Math.Clamp(trimmed.Length * 250 + 10_000, 15_000, 300_000);
                using var safetyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                safetyTimeoutCts.CancelAfter(maxPlaybackMs);

                await playbackFinishedTcs.Task.WaitAsync(safetyTimeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                mediaPlayer.MediaEnded -= onEnded;
                mediaPlayer.MediaFailed -= onFailed;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelled via Stop(), a newer SpeakAsync() call, or safety timeout — exit cleanly.
        }
        finally
        {
            lock (_sessionLock)
            {
                session.MarkCompleted();
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }

            DisposePlayerSafe(mediaPlayer);
            DisposeSafe(mediaSource);
            DisposeSafe(synthesisStream);
            DisposeSafe(synthesizer);
            session.Dispose();
        }
    }

    public void Stop()
    {
        lock (_sessionLock)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
        }
    }

    public static VoiceInformation? ResolveBestVoice(LanguageCode language)
    {
        IReadOnlyList<VoiceInformation> allVoices;
        VoiceInformation? defaultVoice;

        try
        {
            allVoices = SpeechSynthesizer.AllVoices;
            defaultVoice = SpeechSynthesizer.DefaultVoice;
        }
        catch
        {
            return null;
        }

        if (allVoices is null || allVoices.Count == 0)
        {
            return defaultVoice;
        }

        if (language.IsAuto)
        {
            return defaultVoice ?? allVoices[0];
        }

        // 1. If user's default system voice already matches the requested language, prefer it.
        if (defaultVoice is not null && LanguageCode.FromBcp47(defaultVoice.Language) == language)
        {
            return defaultVoice;
        }

        // 2. Exact LanguageCode match (prefer Natural/Online voices when installed on Windows 11).
        VoiceInformation? firstExact = null;
        foreach (var voice in allVoices)
        {
            if (LanguageCode.FromBcp47(voice.Language) == language)
            {
                if (IsPreferredNeuralVoice(voice))
                {
                    return voice;
                }

                firstExact ??= voice;
            }
        }

        if (firstExact is not null)
        {
            return firstExact;
        }

        // 3. Primary BCP-47 language subtag prefix match (e.g. zh-Hans <-> zh-Hant fallback if only one is installed).
        var primaryPrefix = ExtractPrimarySubtag(language.Value);
        if (!string.IsNullOrEmpty(primaryPrefix))
        {
            foreach (var voice in allVoices)
            {
                if (string.Equals(ExtractPrimarySubtag(voice.Language), primaryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return voice;
                }
            }
        }

        return defaultVoice ?? allVoices[0];
    }

    private LanguageCode ResolveEffectiveLanguage(string text, LanguageCode? explicitLanguage)
    {
        if (explicitLanguage is { IsAuto: false } specified)
        {
            return specified;
        }

        if (_languageDetector is not null)
        {
            try
            {
                var detected = _languageDetector.Detect(text);
                if (!detected.Language.IsAuto)
                {
                    return detected.Language;
                }
            }
            catch
            {
                // Fall back to lightweight script scan below.
            }
        }

        return InferLanguageFromScript(text);
    }

    internal static LanguageCode InferLanguageFromScript(string text)
    {
        var cjkCount = 0;
        var kanaCount = 0;
        var hangulCount = 0;
        var cyrillicCount = 0;
        var arabicCount = 0;
        var latinCount = 0;

        foreach (var ch in text)
        {
            if (ch is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
            {
                kanaCount++;
            }
            else if (ch is >= '\uAC00' and <= '\uD7AF')
            {
                hangulCount++;
            }
            else if (ch is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF'))
            {
                cjkCount++;
            }
            else if (ch is >= '\u0400' and <= '\u04FF')
            {
                cyrillicCount++;
            }
            else if (ch is >= '\u0600' and <= '\u06FF')
            {
                arabicCount++;
            }
            else if (char.IsLetter(ch))
            {
                latinCount++;
            }
        }

        if (kanaCount > 0) return LanguageCode.Japanese;
        if (hangulCount > 0) return LanguageCode.Korean;
        if (cjkCount > 0 && cjkCount * 3 >= latinCount) return LanguageCode.ChineseSimplified;
        if (cyrillicCount > latinCount) return LanguageCode.Russian;
        if (arabicCount > latinCount) return LanguageCode.Arabic;
        if (latinCount > 0) return LanguageCode.English;
        if (cjkCount > 0) return LanguageCode.ChineseSimplified;

        return LanguageCode.Auto;
    }

    private void StopLocked()
    {
        var current = _activeSession;
        if (current is null)
        {
            return;
        }

        _activeSession = null;
        current.StopImmediate();
    }

    private static bool IsPreferredNeuralVoice(VoiceInformation voice) =>
        voice.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
        voice.DisplayName.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
        voice.DisplayName.Contains("Online", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPrimarySubtag(string? bcp47Tag)
    {
        if (string.IsNullOrWhiteSpace(bcp47Tag))
        {
            return string.Empty;
        }

        var trimmed = bcp47Tag.Trim();
        var dashIdx = trimmed.IndexOfAny(['-', '_']);
        return dashIdx > 0 ? trimmed[..dashIdx] : trimmed;
    }

    private static void DisposePlayerSafe(MediaPlayer? player)
    {
        if (player is null)
        {
            return;
        }

        try
        {
            player.Pause();
            player.Source = null;
        }
        catch
        {
            // Ignore shutdown races on MediaPlayer.
        }

        DisposeSafe(player);
    }

    private static void DisposeSafe(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Best-effort resource cleanup.
        }
    }

    private sealed class PlaybackSessionState : IDisposable
    {
        private readonly CancellationTokenSource _stopCts = new();
        private readonly CancellationTokenSource _linkedCts;
        private MediaPlayer? _player;
        private volatile bool _isCompleted;
        private volatile bool _stoppedByCaller;
        private bool _disposed;

        public PlaybackSessionState(CancellationToken externalToken)
        {
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token, externalToken);
        }

        public CancellationToken CombinedToken => _linkedCts.Token;

        public bool IsCompleted => _isCompleted;

        public bool IsStoppedByCaller => _stoppedByCaller;

        public void AttachPlayer(MediaPlayer player)
        {
            _player = player;
        }

        public void StopImmediate()
        {
            _stoppedByCaller = true;
            _isCompleted = true;

            try
            {
                _stopCts.Cancel();
            }
            catch
            {
                // Ignore if already disposed.
            }

            var player = Interlocked.Exchange(ref _player, null);
            if (player is not null)
            {
                try
                {
                    player.Pause();
                    player.Source = null;
                }
                catch
                {
                    // Ignore concurrent player teardown.
                }
            }
        }

        public void MarkCompleted()
        {
            _isCompleted = true;
            Interlocked.Exchange(ref _player, null);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isCompleted = true;
            Interlocked.Exchange(ref _player, null);

            try
            {
                _linkedCts.Dispose();
            }
            catch
            {
                // Best-effort.
            }

            try
            {
                _stopCts.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
