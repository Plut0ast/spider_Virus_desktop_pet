namespace WebCrawler;

/// <summary>
/// The parts of a creature's look that the rest of the app needs, whatever kind of creature it is.
/// Each creature type has its own appearance class in its Appearance folder; how it draws itself
/// out on the desktop is up to that class and its behaviour.
/// </summary>
interface ICreatureAppearance
{
    // What to call this kind of creature, e.g. "Spider".
    string DisplayName { get; }

    // The colour that shows how comfortable it is with you (0 wary, 1 at ease).
    Color ComfortColor(float comfort, int alpha = 255);

    // Curled up asleep, as shown in the nest window and on its stats card.
    void DrawResting(Graphics g, PointF center, float size, float comfort, float time);
}
