using Parrot.Config;
using Parrot.Security;

namespace Parrot.Skills;

internal sealed class SkillCatalog(
    IReadOnlyList<SkillRoot> roots,
    Func<(SkillConfiguration Configuration, long Generation)> configuration)
{
    private readonly Lock _gate = new();
    private Action<string, bool>? _configure;
    private SkillSnapshot? _snapshot;
    private SecurityProfile? _security;
    private int _generation;
    private int _snapshotGeneration = -1;
    private long _configurationGeneration = -1;

    internal IReadOnlyList<SkillRoot> Roots { get; } = roots ?? throw new ArgumentNullException(nameof(roots));

    public SkillSnapshot Capture(SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(security);
        lock (_gate)
        {
            var configured = configuration();
            if (_snapshot is not null
                && _snapshotGeneration == _generation
                && _configurationGeneration == configured.Generation
                && ReferenceEquals(_security, security))
            {
                return _snapshot;
            }

            _snapshot = SkillDiscovery.Discover(Roots, configured.Configuration, security);
            _security = security;
            _snapshotGeneration = _generation;
            _configurationGeneration = configured.Generation;
            return _snapshot;
        }
    }

    public SkillSnapshot Refresh(SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(security);
        lock (_gate)
        {
            _generation++;
        }

        return Capture(security);
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

    internal SkillCatalog WithConfiguration(Action<string, bool> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }
}
