namespace WebCrawler;

/// <summary>Options from the tray menu.</summary>
sealed class CrawlerSettings
{
    public float Intensity = 1f;
    public bool Chase = true;
    public bool Flies = true;
    public bool Paused;
    public float Speed = 130f;
}
