using System.Text;

namespace Parrot.Tools.ApplyPatch;

internal static class AiderPatchParser
{
    private const string SearchMarker = "<<<<<<< SEARCH";
    private const string ReplaceMarker = ">>>>>>> REPLACE";

    public static Patch Parse(string text)
    {
        var lines = PatchText.Lines(text);
        var blocks = ReadBlocks(lines);
        var builders = new Dictionary<string, OperationBuilder>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var block in blocks)
        {
            if (!builders.TryGetValue(block.Path, out var operation))
            {
                operation = new OperationBuilder(block.Path);
                builders.Add(block.Path, operation);
                order.Add(block.Path);
            }

            operation.Add(block);
        }

        return Patch.Validate([.. order.Select(path => builders[path].Build())]);
    }

    private static List<Block> ReadBlocks(string[] lines)
    {
        var blocks = new List<Block>();
        var path = string.Empty;

        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index].Trim();

            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (!string.Equals(line, SearchMarker, StringComparison.Ordinal))
            {
                path = line;

                try
                {
                    Patch.ValidatePath(path);
                }
                catch (PatchException failure)
                {
                    throw new PatchException($"Invalid patch at line {index + 1}: {failure.Message}", failure);
                }

                index++;
                continue;
            }

            if (path.Length == 0)
            {
                throw new PatchException($"Invalid patch at line {index + 1}: block has no file path.");
            }

            var blockLine = index + 1;
            var searchStart = ++index;

            while (index < lines.Length && !IsDivider(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                throw new PatchException(
                    $"Invalid patch at line {blockLine}: SEARCH section is missing =======.");
            }

            var search = lines.Skip(searchStart).Take(index - searchStart).ToArray();
            var replaceStart = ++index;

            while (index < lines.Length && !string.Equals(lines[index].Trim(), ReplaceMarker, StringComparison.Ordinal))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                throw new PatchException(
                    $"Invalid patch at line {blockLine}: section is missing {ReplaceMarker}.");
            }

            var replace = lines.Skip(replaceStart).Take(index - replaceStart).ToArray();
            blocks.Add(new Block(path, blockLine, search, replace));
            index++;
        }

        if (blocks.Count == 0)
        {
            throw new PatchException("Patch has no SEARCH/REPLACE blocks.");
        }

        return blocks;
    }

    private static bool IsDivider(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 7 && trimmed.All(character => character == '=');
    }

    private sealed record Block(
        string Path,
        int Line,
        IReadOnlyList<string> Search,
        IReadOnlyList<string> Replace);

    private sealed class OperationBuilder(string path)
    {
        private readonly List<PatchHunk> _hunks = [];
        private PatchOperationKind? _kind;
        private string _data = string.Empty;

        public void Add(Block block)
        {
            if (block.Search.Count == 0)
            {
                if (_kind is not null)
                {
                    throw MixedOperation(block);
                }

                _kind = PatchOperationKind.Add;
                var data = new StringBuilder();

                foreach (var line in block.Replace)
                {
                    _ = data.Append(line).Append('\n');
                }

                _data = data.ToString();
                return;
            }

            if (_kind == PatchOperationKind.Add)
            {
                throw MixedOperation(block);
            }

            _kind = PatchOperationKind.Update;
            var lines = new List<PatchLine>(block.Search.Count + block.Replace.Count);
            lines.AddRange(block.Search.Select(line => new PatchLine('-', line)));
            lines.AddRange(block.Replace.Select(line => new PatchLine('+', line)));
            _hunks.Add(new PatchHunk(lines));
        }

        public PatchOperation Build() =>
            new(_kind ?? PatchOperationKind.Update, path, _data, _hunks);

        private PatchException MixedOperation(Block block) =>
            new($"Invalid patch at line {block.Line}: '{path}' mixes file creation and updates.");
    }
}
