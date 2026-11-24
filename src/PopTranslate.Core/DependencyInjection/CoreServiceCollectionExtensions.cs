using PopTranslate.Core.Configuration;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Language;
using PopTranslate.Core.Networking;
using PopTranslate.Core.Ocr;
using PopTranslate.Core.Polish;
using PopTranslate.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PopTranslate.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers all PopTranslate.Core domain services, providers, and orchestrators.
    /// </summary>
    public static IServiceCollection AddPopTranslateCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.TryAddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<ISecretProtector>()));
        services.TryAddSingleton<IProxyHttpClientFactory, ProxyHttpClientFactory>();

        services.TryAddSingleton<ILanguageDetector, LocalLanguageDetector>();
        services.TryAddSingleton<ILanguageRouter, DualTargetLanguageRouter>();
        services.TryAddSingleton<ITextPreprocessor, TextPreprocessor>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITranslationProvider, OpenAiCompatibleTranslationProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITranslationProvider, FreeTraditionalTranslationProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITranslationProvider, DeepLTranslationProvider>());
        services.TryAddSingleton<ITranslationOrchestrator, TranslationOrchestrator>();
        services.TryAddSingleton<IBackTranslationService, BackTranslationService>();
        services.TryAddSingleton<IModelDiscoveryService, OpenAiModelDiscoveryService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolishProvider, OpenAiCompatiblePolishProvider>());
        services.TryAddSingleton<IPolishOrchestrator, PolishOrchestrator>();
        services.TryAddSingleton<ITextDiffService, LocalTextDiffService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOcrProvider, VisionLlmOcrProvider>());
        services.TryAddSingleton<IOcrOrchestrator, OcrOrchestrator>();

        return services;
    }
}
