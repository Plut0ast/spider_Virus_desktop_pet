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

    // The nest window: dusty silk and paper tags on near-black, so the only real colour
    // on screen is each creature's own comfort colour.
    public static readonly Color WindowBack = Color.FromArgb(17, 16, 15);
    public static readonly Color Silk = Color.FromArgb(214, 207, 192);
    public static readonly Color NestBulk = Color.FromArgb(27, 25, 23);
    public static readonly Color HollowEdge = Color.FromArgb(36, 33, 30);
    public static readonly Color HollowCentre = Color.FromArgb(7, 7, 7);
    public static readonly Color Paper = Color.FromArgb(228, 221, 205);
    public static readonly Color Ink = Color.FromArgb(48, 43, 38);
    public static readonly Color InkMuted = Color.FromArgb(118, 109, 98);
    public static readonly Color Text = Color.FromArgb(226, 220, 208);
    public static readonly Color MutedText = Color.FromArgb(128, 121, 111);
}
