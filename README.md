# Combat Log Viewer

A tool I vibe coded because manually keeping track of old runs in my Combat Logs was annoying. Requires proprietary ZZZ Analaytics logger. Might randomly break if logger format changes.

Features:
- Displays and sorts all runs from your Combat Log folder
- Displays basic run info including characters, mindscapes, weapons, disc sets, mainstats, substats, score, time taken
- Drag and drop runs from the app straight to Analytics drop box
- OBS support for automatically recording and linking run videos
- Sorting features for runs, including search

There are two builds of the same app:

- **Standalone** (`dist\standalone`, ~62 MB): runs on any 64-bit Windows 10/11 PC with nothing to install. Almost all of its size is the bundled .NET runtime, already compressed. The app has no images built in.
- **Small** (`dist\small`, ~250 KB): needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed (a one-time ~55 MB install). If it's missing, Windows shows a prompt with the download link.

Icons are cached at 192px (about 70 KB each), so the cache stays a few MB.

## Building

Requires the .NET 10 SDK.

```
dotnet build                                   # debug build in bin\Debug
dotnet publish -c Release -o dist\standalone   # standalone exe
dotnet publish -c Release -o dist\small -p:SelfContained=false -p:EnableCompressionInSingleFile=false
```
