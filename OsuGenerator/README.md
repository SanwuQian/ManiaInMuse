# ManiaInMuse OsuGenerator

Offline CSV to osu!mania converter for maps exported by `MapLoader`.

## Usage

```powershell
dotnet run --project "D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\OsuGenerator" -- "D:\APP Profile\steam\steamapps\common\Muse Dash\UserData\ManiaInMuse\maps\latest.csv" --bpm 120
```

Output defaults to the input path with `.osu` extension. You can also pass an
explicit output path:

```powershell
dotnet run --project "D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\OsuGenerator" -- "input.csv" "output.osu" --bpm 180 --title "Converted"
```

## Current rules

- `monster` and `ghost`: active taps.
- `hold`: active hold, keeps the character in its air/ground posture until end.
- `boss`: one tap, air/ground does not matter.
- `multi`: generated as BPM-aware chords, ignoring every other note during the section.
- `music`: collected if the simulated character posture matches within `+-50ms`; otherwise a tap is inserted.
- `block`: avoided if the simulated character would be in the unsafe posture within `+-120ms`; otherwise a dodge tap is inserted.
