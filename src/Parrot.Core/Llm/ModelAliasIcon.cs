namespace Parrot.Llm;

internal sealed record ModelAliasIcon(string Glyph, ModelAliasIconColor Color)
{
    public static ModelAliasIcon Parse(string glyph, string color)
    {
        var parsed = color switch
        {
            "black" => ModelAliasIconColor.Black,
            "red" => ModelAliasIconColor.Red,
            "green" => ModelAliasIconColor.Green,
            "yellow" => ModelAliasIconColor.Yellow,
            "blue" => ModelAliasIconColor.Blue,
            "magenta" => ModelAliasIconColor.Magenta,
            "cyan" => ModelAliasIconColor.Cyan,
            "white" => ModelAliasIconColor.White,
            "gray" => ModelAliasIconColor.Gray,
            _ => throw new InvalidDataException($"unsupported model alias icon color {color}"),
        };

        return new(glyph, parsed);
    }
}
