using System.Threading.Channels;
using Parrot.Cli.Commands;
using Parrot.Web.Protocol;

namespace Parrot.Cli.Web;

internal sealed class WebApplicationExit(ChannelWriter<SlashFrame> frames, CancellationTokenSource source) : IApplicationExit
{
    public async Task Exit(CancellationToken cancellationToken)
    {
        await frames.WriteAsync(new SlashFrame { Exited = new SlashExited() }, cancellationToken).ConfigureAwait(false);
        await source.CancelAsync().ConfigureAwait(false);
    }
}
