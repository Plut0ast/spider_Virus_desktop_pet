namespace WebCrawler;

static class Palette
{
    // Glitch highlight colours.
    public static readonly Color Pink = Color.FromArgb(255, 63, 164);
    public static readonly Color Blue = Color.FromArgb(61, 90, 254);
    public static readonly Color Cyan = Color.FromArgb(77, 216, 255);
    public static readonly Color Yellow = Color.FromArgb(255, 225, 77);
    public static readonly Color Lime = Color.FromArgb(125, 255, 106);
    public static readonly Color[] All = { Pink, Blue, Cyan, Yellow, Lime };
    public static Color Pick(Random rng) => All[rng.Next(All.Length)];

    // Shared node-style linework for webs, flies and the nest.
    public static readonly Color Line = Color.FromArgb(150, 172, 255);
    public static readonly Color BodyRed = Color.FromArgb(255, 64, 96);
    public static readonly Color NodeFill = Color.FromArgb(12, 14, 30);

    // Stat bars.
    public static readonly Color Hunger = Color.FromArgb(255, 170, 70);

    // The nest window.
    public static readonly Color WindowBack = Color.FromArgb(14, 15, 24);
    public static readonly Color Card = Color.FromArgb(22, 24, 39);
    public static readonly Color CardEdge = Color.FromArgb(38, 41, 62);
    public static readonly Color Text = Color.FromArgb(230, 232, 245);
    public static readonly Color MutedText = Color.FromArgb(138, 143, 176);
}
