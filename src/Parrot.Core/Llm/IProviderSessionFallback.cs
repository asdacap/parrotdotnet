namespace Parrot.Llm;

internal interface IProviderSessionFallback
{
    ValueTask FallBackToHttp();
}
