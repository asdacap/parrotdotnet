using Parrot.Llm;

namespace Parrot.Protocol;

public sealed partial class TurnModelAliasIcon
{
    internal static TurnModelAliasIcon FromAliasIcon(ModelAliasIcon icon) => new()
    {
        Glyph = icon.Glyph,
        Color = icon.Color switch
        {
            ModelAliasIconColor.Black => TurnModelAliasIconColor.Black,
            ModelAliasIconColor.Red => TurnModelAliasIconColor.Red,
            ModelAliasIconColor.Green => TurnModelAliasIconColor.Green,
            ModelAliasIconColor.Yellow => TurnModelAliasIconColor.Yellow,
            ModelAliasIconColor.Blue => TurnModelAliasIconColor.Blue,
            ModelAliasIconColor.Magenta => TurnModelAliasIconColor.Magenta,
            ModelAliasIconColor.Cyan => TurnModelAliasIconColor.Cyan,
            ModelAliasIconColor.White => TurnModelAliasIconColor.White,
            ModelAliasIconColor.Gray => TurnModelAliasIconColor.Gray,
            _ => throw new InvalidOperationException("The model alias icon color is invalid."),
        },
    };
}
