using System.Globalization;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class ToolCycleImageBudget(int maximumBytes, IPromptTemplateCatalog promptTemplates)
{
    // read_image calls in one batch run in parallel and share this budget.
    private readonly Lock _gate = new();
    private long _acceptedBytes;
    private bool _isExceeded;

    public bool IsExceeded
    {
        get
        {
            lock (_gate)
            {
                return _isExceeded;
            }
        }
    }

    public bool TryAccept(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        lock (_gate)
        {
            if (_isExceeded || byteLength > maximumBytes - _acceptedBytes)
            {
                _isExceeded = true;
                return false;
            }

            _acceptedBytes += byteLength;
            return true;
        }
    }

    public void RestoreAccepted(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        lock (_gate)
        {
            _acceptedBytes = checked(_acceptedBytes + byteLength);
        }
    }

    public void RestoreExceeded()
    {
        lock (_gate)
        {
            _isExceeded = true;
        }
    }

    public string DescribeFailure() => promptTemplates.Render(
        "tool-result.image-budget-exceeded",
        [new PromptTemplateArgument("limit_bytes", maximumBytes.ToString(CultureInfo.InvariantCulture))]);
}
