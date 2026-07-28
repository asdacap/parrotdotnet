namespace Parrot.Cli.Enhanced;

internal interface ILiveBufferItem
{
    MultiLine Render(LiveBufferRenderContext context);
}
