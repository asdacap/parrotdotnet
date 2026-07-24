namespace Parrot.Process;

internal static class ProcessResultFormatter
{
    public static string Format(ProcessResult result)
    {
        if (result.Spilled)
        {
            return result.BlobPath;
        }

        var text = new System.Text.StringBuilder();
        _ = text.Append("Process exited with code ").Append(result.ExitCode);
        AppendOutput(text, "stdout", result.Stdout);
        AppendOutput(text, "stderr", result.Stderr);
        return text.ToString();
    }

    private static void AppendOutput(System.Text.StringBuilder text, string name, string output)
    {
        if (output.Length > 0)
        {
            _ = text.Append('\n').Append('[').Append(name).Append("]\n").Append(output);
        }
    }
}
