namespace PopTranslate.Core.Models;

public enum ProviderKind
{
    FreeTraditional = 0,
    OpenAiCompatible = 1,
    DeepL = 2,
}

public enum DeepLEndpointTier
{
    Auto = 0,
    Free = 1,
    Pro = 2,
}

public enum FreeTranslatorEngine
{
    Auto = 0,
    Google = 1,
    Bing = 2,
}
