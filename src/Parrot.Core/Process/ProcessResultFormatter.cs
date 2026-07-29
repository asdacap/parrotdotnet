namespace Parrot.Process;

internal static class ProcessResultFormatter
{
    public static string Format(ProcessResult result)
    {
        if (result.Spilled)
        {
            return $"Process exited with code {result.ExitCode}\n"
                + $"Tool output exceeded 64 KiB and was saved to {result.BlobPath}. "
                + "Use exec_command to read the file.";
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
