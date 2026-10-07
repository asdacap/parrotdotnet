using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Process;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class MonitorToolFactory(
    IProcessOwner processes,
    IPromptTemplateCatalog templates,
    ToolOutputBlobStore outputBlobs,
    IDiagnosticLog diagnostics,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("monitor");

    public ITool Create(IAgentSession session) =>
        new MonitorTool(processes, session, templates, outputBlobs, diagnostics);
}
