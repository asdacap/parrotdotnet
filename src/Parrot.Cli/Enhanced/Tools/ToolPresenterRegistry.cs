namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolPresenterRegistry
{
    private readonly GenericToolPresenter _fallback;
    private readonly Dictionary<string, IToolPresenter> _presenters;

    public ToolPresenterRegistry(IReadOnlyList<IToolPresenter> presenters, GenericToolPresenter fallback)
    {
        ArgumentNullException.ThrowIfNull(presenters);
        ArgumentNullException.ThrowIfNull(fallback);

        var registered = new Dictionary<string, IToolPresenter>(StringComparer.Ordinal);
        foreach (var presenter in presenters)
        {
            ArgumentNullException.ThrowIfNull(presenter);
            if (presenter.ToolName.Length == 0)
            {
                throw new ArgumentException("A tool presenter name cannot be empty.", nameof(presenters));
            }

            if (!registered.TryAdd(presenter.ToolName, presenter))
            {
                throw new ArgumentException($"A presenter for '{presenter.ToolName}' is already registered.", nameof(presenters));
            }
        }

        _presenters = registered;
        _fallback = fallback;
    }

    public ToolPresentationMetadata Describe(string toolName) => Find(toolName).Metadata;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var presenter = Find(call.ToolName);
        var redactedCall = ToolPresentationRedactor.Redact(call, presenter.Metadata);
        try
        {
            return presenter.PresentLive(redactedCall, frame);
        }
        catch when (!ReferenceEquals(presenter, _fallback))
        {
            return _fallback.PresentLive(redactedCall, frame);
        }
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var presenter = Find(call.ToolName);
        var metadata = presenter.Metadata;
        var redactedCall = ToolPresentationRedactor.Redact(call, metadata);
        var redactedTerminal = ToolPresentationRedactor.Redact(terminal, metadata);
        try
        {
            return presenter.PresentTerminal(redactedCall, redactedTerminal);
        }
        catch when (!ReferenceEquals(presenter, _fallback))
        {
            return _fallback.PresentTerminal(redactedCall, redactedTerminal);
        }
    }

    private IToolPresenter Find(string toolName) =>
        _presenters.TryGetValue(toolName, out var presenter) ? presenter : _fallback;
}
