namespace WebCrawler;

/// <summary>
/// Every creature type the app knows, by the Kind name saved in the memory file.
/// Register a new creature here.
/// </summary>
static class CreatureFactory
{
    public const string DefaultKind = Spider.KindName;

    public static Creature Create(World world, CreatureMemory memory) => memory.Kind switch
    {
        Spider.KindName => new Spider(world, memory),
        _ => new Spider(world, memory),
    };
}
