using Parrot.Llm;

namespace Parrot.Config;

internal sealed record ModelPresetSelection(
    ModelPresetConfig Preset,
    ResolvedModelSelection ResolvedSelection);
