using System.Globalization;

namespace Parrot.Process;

internal static class ProcessResultFormatter
{
    public static string Format(ProcessResult result)
    {
        var completion = FormatCompletion(result.ExitCode, result.ElapsedMilliseconds);

        if (result.Spilled)
        {
            return completion + "\n"
                + $"Tool output exceeded 64 KiB and was saved to {result.BlobPath}.";
        }

        var text = new System.Text.StringBuilder(completion);
        AppendOutput(text, "stdout", result.Stdout);
        AppendOutput(text, "stderr", result.Stderr);
        return text.ToString();
    }

    public static string FormatCompletion(int exitCode, long elapsedMilliseconds)
    {
        var seconds = Math.Max(0L, elapsedMilliseconds) / 1000d;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Process exited with code {exitCode} after {seconds:0.00}s");
    }

    private static void AppendOutput(System.Text.StringBuilder text, string name, string output)
    {
        if (output.Length > 0)
        {
            _ = text.Append('\n').Append('[').Append(name).Append("]\n").Append(output);
        }
    }
}
