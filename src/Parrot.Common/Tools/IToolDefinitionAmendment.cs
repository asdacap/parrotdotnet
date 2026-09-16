namespace Parrot.Tools;

/// <summary>Rewrites configured tool definitions before they are offered to the model.</summary>
internal interface IToolDefinitionAmendment
{
    /// <summary>Returns a catalog with this amendment applied; the given catalog is unchanged.</summary>
    ToolDefinitionCatalog Amend(ToolDefinitionCatalog catalog);
}
