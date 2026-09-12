using System.Globalization;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class ToolCycleImageBudget(int maximumBytes, IPromptTemplateCatalog promptTemplates)
{
    private long _acceptedBytes;

    public bool IsExceeded { get; private set; }

    public bool TryAccept(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        if (IsExceeded || byteLength > maximumBytes - _acceptedBytes)
        {
            IsExceeded = true;
            return false;
        }

        _acceptedBytes += byteLength;
        return true;
    }

    public void RestoreAccepted(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        _acceptedBytes = checked(_acceptedBytes + byteLength);
    }

    public void RestoreExceeded() => IsExceeded = true;

    public string DescribeFailure() => promptTemplates.Render(
        "tool-result.image-budget-exceeded",
        [new PromptTemplateArgument("limit_bytes", maximumBytes.ToString(CultureInfo.InvariantCulture))]);
}
