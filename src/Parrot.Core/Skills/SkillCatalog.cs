using Parrot.Config;

namespace Parrot.Skills;

internal sealed class SkillCatalog(
    IReadOnlyList<SkillRoot> roots,
    Func<(SkillConfiguration Configuration, long Generation)> configuration) : ISkillCatalog
{
    private readonly Lock _gate = new();
    private Action<string, bool>? _configure;
    private SkillSnapshot? _snapshot;
    private int _generation;
    private int _snapshotGeneration = -1;
    private long _configurationGeneration = -1;

    internal IReadOnlyList<SkillRoot> Roots { get; } = roots ?? throw new ArgumentNullException(nameof(roots));

    public SkillSnapshot Capture()
    {
        lock (_gate)
        {
            var configured = configuration();
            if (_snapshot is not null
                && _snapshotGeneration == _generation
                && _configurationGeneration == configured.Generation)
            {
                return _snapshot;
            }

            _snapshot = SkillDiscovery.Discover(Roots, configured.Configuration);
            _snapshotGeneration = _generation;
            _configurationGeneration = configured.Generation;
            return _snapshot;
        }
    }

    public SkillSnapshot Refresh()
    {
        lock (_gate)
        {
            _generation++;
        }

        return Capture();
    }

    public void Configure(string path, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        (_configure ?? throw new InvalidOperationException("skill configuration is unavailable"))(path, enabled);
        Invalidate();
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
        }
    }

    internal ISkillCatalog WithConfiguration(Action<string, bool> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }
}
