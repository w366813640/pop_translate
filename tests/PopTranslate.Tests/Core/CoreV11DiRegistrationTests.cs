using PopTranslate.Core.Contracts;
using PopTranslate.Core.DependencyInjection;
using PopTranslate.Core.Diff;
using PopTranslate.Core.Polish;
using PopTranslate.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PopTranslate.Tests.Core;

public sealed class CoreV11DiRegistrationTests
{
    [Fact]
    public void AddPopTranslateCore_RegistersAllFourV11ServicesAndProviders()
    {
        var services = new ServiceCollection();
        services.AddPopTranslateCore();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.IsType<LocalTextDiffService>(provider.GetRequiredService<ITextDiffService>());
        Assert.IsType<BackTranslationService>(provider.GetRequiredService<IBackTranslationService>());
        Assert.IsType<PolishOrchestrator>(provider.GetRequiredService<IPolishOrchestrator>());

        var singlePolishProvider = provider.GetRequiredService<IPolishProvider>();
        Assert.IsType<OpenAiCompatiblePolishProvider>(singlePolishProvider);

        var allPolishProviders = provider.GetServices<IPolishProvider>().ToList();
        Assert.Single(allPolishProviders);
        Assert.IsType<OpenAiCompatiblePolishProvider>(allPolishProviders[0]);
    }
}
