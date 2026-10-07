# Web Crawler

A desktop pet for Windows. A spider walks around on top of all your windows, and whatever its feet land on glitches: split colours, shifted slices, shuffled tiles, highlight bars, outline boxes and enlarged copies of the text. It's click-through, so you can keep working underneath it.

## Run

Double-click `publish\WebCrawler.exe`. It needs the .NET 8 Desktop Runtime.

## Controls

- **Hold Esc for 1 second** to remove all spiders and quit.
- **Right-click the tray icon** to add or remove spiders (up to 6), turn cursor-chasing on or off, set glitch intensity, pause, or quit.
- **Double-click the tray icon** to add another spider.

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
