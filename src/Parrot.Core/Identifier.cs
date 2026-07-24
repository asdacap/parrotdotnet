using System.Security.Cryptography;

namespace Parrot;

// Haikunator's scheme: adjective-noun-token, prefixed by what the thing is.
//
//   user-session-quiet-heron-3f4a91
//   agent-session-bold-otter-c07d12
//
// The scheme rather than the package: Haikunator hard-codes its own format and
// this needs a kind prefix, and MIGRATION.md section 2 prefers no dependency
// over a small one. Two word lists and a token is the whole of it.
//
// The token is what makes a session id safe as well as readable: words alone
// give 64*64 combinations, which collide within a single conversation.
//
// Event ids do not use this scheme at all -- see EventId.
internal static class Identifier
{
    private const int TokenLength = 6;

    private static readonly string[] Adjectives =
    [
        "amber", "ancient", "autumn", "bitter", "black", "blue", "bold", "brave",
        "bright", "broad", "calm", "cold", "cool", "crimson", "damp", "dark",
        "dawn", "deep", "divine", "dry", "dusty", "empty", "falling", "fancy",
        "flat", "floral", "fragrant", "frosty", "gentle", "green", "hidden", "holy",
        "icy", "jolly", "late", "lingering", "little", "lively", "long", "loud",
        "lucky", "misty", "muddy", "mute", "nameless", "noisy", "odd", "old",
        "orange", "patient", "plain", "polished", "proud", "purple", "quiet", "rapid",
        "raspy", "restless", "rough", "round", "royal", "shiny", "shrill", "shy",
        "silent", "small", "snowy", "soft", "solitary", "sparkling", "spring", "square",
        "steep", "still", "summer", "super", "sweet", "throbbing", "tight", "tiny",
        "twilight", "wandering", "weathered", "white", "wild", "winter", "wispy", "withered",
        "yellow", "young",
    ];

    private static readonly string[] Nouns =
    [
        "art", "band", "bar", "base", "bird", "block", "boat", "bonus",
        "bread", "breeze", "brook", "bush", "butterfly", "cake", "cell", "cherry",
        "cloud", "credit", "darkness", "dawn", "dew", "disk", "dream", "dust",
        "feather", "field", "fire", "firefly", "flower", "fog", "forest", "frog",
        "frost", "glade", "glitter", "grass", "hall", "hat", "haze", "heart",
        "hill", "king", "lab", "lake", "leaf", "limit", "math", "meadow",
        "mode", "moon", "morning", "mountain", "mouse", "mud", "night", "paper",
        "pine", "poetry", "pond", "queen", "rain", "recipe", "resonance", "rice",
        "river", "salad", "scene", "sea", "shadow", "shape", "silence", "sky",
        "smoke", "snow", "snowflake", "sound", "star", "sun", "sunset", "surf",
        "term", "thunder", "tooth", "tree", "truth", "union", "unit", "violet",
        "voice", "water", "waterfall", "wave", "wildflower", "wind", "wood",
    ];

    public static string UserSession() => Compose("user-session");

    public static string AgentSession() => Compose("agent-session");

    // UUIDv7, not a word triple and not a v4 GUID. Events are minted per token
    // delta and nobody reads one, so readability buys nothing -- while v7's
    // leading timestamp means successive inserts land next to each other in the
    // index on event.id rather than scattering across it, which a v4 does.
    public static string EventId() => Opaque();

    // Both share the event id's scheme for the same reason: they are minted per
    // prompt, indexed, and read by machines. A message id is only ever minted
    // when the sender did not supply one -- it is the sender's name for their
    // prompt, which is what makes admitting it twice detectable.
    public static string MessageId() => $"msg-{Opaque()}";

    public static string InputId() => $"inp-{Opaque()}";

    private static string Opaque() =>
        Guid.CreateVersion7().ToString("n", System.Globalization.CultureInfo.InvariantCulture);

    private static string Compose(string kind)
    {
        var adjective = Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)];
        var noun = Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)];
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenLength / 2));

        return $"{kind}-{adjective}-{noun}-{token}";
    }
}
