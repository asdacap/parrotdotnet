using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed record LiveModelAliasIcon(string Glyph, ModelAliasIconColor Color)
{
    public static LiveModelAliasIcon? Convert(TurnModelAliasIcon? icon)
    {
        if (icon is null)
        {
            return null;
        }

        var color = icon.Color switch
        {
            TurnModelAliasIconColor.Black => ModelAliasIconColor.Black,
            TurnModelAliasIconColor.Red => ModelAliasIconColor.Red,
            TurnModelAliasIconColor.Green => ModelAliasIconColor.Green,
            TurnModelAliasIconColor.Yellow => ModelAliasIconColor.Yellow,
            TurnModelAliasIconColor.Blue => ModelAliasIconColor.Blue,
            TurnModelAliasIconColor.Magenta => ModelAliasIconColor.Magenta,
            TurnModelAliasIconColor.Cyan => ModelAliasIconColor.Cyan,
            TurnModelAliasIconColor.White => ModelAliasIconColor.White,
            TurnModelAliasIconColor.Gray => ModelAliasIconColor.Gray,
            _ => throw new InvalidOperationException("The model alias icon color is invalid."),
        };
        return new LiveModelAliasIcon(icon.Glyph, color);
    }
}
