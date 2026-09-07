namespace Parrot.Cli.Enhanced;

/// <summary>A live display item rendered for the current terminal geometry and palette.</summary>
internal interface ILiveBufferItem
{
    MultiLine Render(LiveBufferRenderContext context);

    /// <summary>Returns the item at an absolute animation frame; stationary items remain unchanged.</summary>
    ILiveBufferItem Animate(int frame) => this;

    /// <summary>Captures a relative animation origin; items without relative animation remain unchanged.</summary>
    ILiveBufferItem CaptureAnimation(int frame) => this;

    /// <summary>Renders animation time relative to a captured origin; other items remain unchanged.</summary>
    ILiveBufferItem AnimateSinceCapture(int frame) => this;
}
