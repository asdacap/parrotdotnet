namespace Parrot.Config;

internal sealed record AgentSendConfig(bool ToParent, IPromptTemplateCatalog PromptTemplates);
