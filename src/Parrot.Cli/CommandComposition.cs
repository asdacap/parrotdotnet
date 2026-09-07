using Parrot.Auth;
using Parrot.Llm;
using Pure.DI;

namespace Parrot.Cli;

internal partial class CommandComposition
{
    internal static void Setup() =>
        DI.Setup(nameof(CommandComposition))
            .Hint(Hint.Resolve, "Off")
            .Arg<Interrupts>("interrupts")
            .Arg<TextWriter>("output", "output")
            .Arg<TextWriter>("error", "error")
            .Bind().As(Lifetime.Singleton).To(_ =>
                new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<HttpClient>(out var httpClient);
                return new ProviderHttpClientCatalog(httpClient);
            })
            .Bind().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<HttpClient>(out var httpClient);
                return new ModelsDevInformationProvider(httpClient);
            })
            .Bind().As(Lifetime.Singleton).To<IBrowserOpener>(_ =>
                new SystemBrowserOpener(System.Diagnostics.Process.Start))
            .Bind().As(Lifetime.Singleton).To(_ => new OpenAiOAuthOptions())
            .Bind<IOAuthClient>().As(Lifetime.Singleton).To(ctx =>
            {
                ctx.Inject<HttpClient>(out var httpClient);
                ctx.Inject<IBrowserOpener>(out var browserOpener);
                ctx.Inject<OpenAiOAuthOptions>(out var options);
                return new OpenAiOAuthClient(httpClient, browserOpener, options);
            })
            .Bind().To(ctx =>
            {
                ctx.Inject<Interrupts>(out var interrupts);
                ctx.Inject<TextWriter>("output", out var output);
                ctx.Inject<TextWriter>("error", out var error);
                ctx.Inject<ProviderHttpClientCatalog>(out var httpClients);
                ctx.Inject<IBrowserOpener>(out var browserOpener);
                ctx.Inject<IOAuthClient>(out var oauthClient);
                ctx.Inject<ModelsDevInformationProvider>(out var modelsDev);
                return new CommandDispatcher(
                    interrupts, output, error, httpClients, browserOpener, oauthClient, modelsDev);
            })
            .Root<CommandDispatcher>("Dispatcher");
}
