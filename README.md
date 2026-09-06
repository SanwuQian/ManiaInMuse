# ManiaInMuse

ManiaInMuse 是一个谱面读取、谱面转换和游戏内可视化播放器，用于 Muse Dash 的 MelonLoader Mod。

激活时会在进入歌曲时读取当前谱面的全部键时刻信息，导出原始 CSV，转换为 osu!mania 风格谱面，并在游戏内显示下落式谱面覆盖层。

可能与其他使用覆盖层的模组发生重叠冲突。

本Mod仅供练习使用，使用期间请离线游玩。切勿在官谱或者自制谱中使用模组并上传成绩。


## 功能

- 进入歌曲时从 `StageBattleComponent` 读取当前 Muse Dash 谱面数据。
- 导出原始谱面 CSV，包含时间、类型、空中/地面、BPM、长按长度、multi 连击参数等字段。
- 为当前歌曲生成 `latest.osu`。
- 在游戏内显示黑色背景的下落式谱面覆盖层。
- 支持 2K 到 7K 的自定义键数。
- 支持通过 `Player.cfg` 配置每个轨道对应空中或地面。
- 支持 `monster`、`ghost`、`hold`、`boss`、`multi`、`music`、`block` 等类型。
- 对 multi 使用基于 BPM 的左右对拍生成规则。
- 使用局部轨道交换和短间隔修复，尽量减少不顺手的密集同轨间隔。
- 暂停、结算、失败、退出歌曲后会隐藏播放器界面。

## 运行环境

- Muse Dash，Il2Cpp 版本。
- MelonLoader `0.7.3` net6 运行环境。
- 如果需要从源码编译，需要安装 .NET 6 SDK。

ManiaInMuse 本身不强依赖 MuseDashMirror 或 CustomAlbums。如果你要游玩自定义专辑，CustomAlbums 等 Mod 仍然需要按它们自己的要求安装。

## 安装

把编译得到的 DLL 和配置文件放到 Muse Dash 目录：

```text
Muse Dash/
  Mods/
    ManiaInMuse.dll
  UserData/
    ManiaInMuse/
      Player.cfg
```

如果 `Player.cfg` 不存在，Mod 启动时会自动创建默认配置。每次进入歌曲时都会重新读取配置，因此运行中修改或删除配置后，无需重启游戏。

## 导出文件

每次进入歌曲后，ManiaInMuse 会把谱面文件写入：

```text
Muse Dash/UserData/ManiaInMuse/maps/
```

生成的文件：

- `latest.csv`：最近一次进入歌曲导出的原始谱面数据。
- `latest.osu`：最近一次转换得到的 osu!mania 谱面。
- `yyyyMMdd_HHmmss_fff_<noteCount>_notes.csv`：带时间戳的历史 CSV 导出缓存。

历史 CSV 会按默认缓存策略自动清理，避免目录无限增长。

## Player.cfg 配置

默认配置示例：

```ini
[Player]
OffsetMs = 0
FallTimeMs = 480
TrackWidth = 480
TrackHeight = 1080
NoteWidth = 120
NoteHeight = 80
PositionX = 0
PositionY = 0
BackgroundColor = 0,0,0,255
NoteColor = 0,220,70,255
HoldColor = 110,110,110,255
JudgementLinePosition = 1
KeyCount = 4

[keys:2]
LaneTypes = A,G
Split = 1

[keys:3]
LaneTypes = A,G,A
Split = 1

[keys:4]
LaneTypes = A,G,A,G
Split = 2

[keys:5]
LaneTypes = A,G,A,G,A
Split = 2

[keys:6]
LaneTypes = A,A,G,G,A,G
Split = 3

[keys:7]
LaneTypes = A,G,A,G,A,G,A
Split = 3
```

参数含义：

- `OffsetMs`：覆盖层时间偏移，单位毫秒，范围 `-1000` 到 `1000`。数值越大，键越晚到达判定线；负数会让键提前。
- `FallTimeMs`：键从顶部生成到判定线的下落时间，单位毫秒。
- `TrackWidth`、`TrackHeight`：覆盖层轨道区域的宽高，基于 1920x1080 参考画布。
- `NoteWidth`、`NoteHeight`：点击键方块的宽高。长按头使用同样大小，长按身体使用 `NoteWidth`。
- `PositionX`、`PositionY`：轨道区域相对屏幕中心的偏移。
- `BackgroundColor`：轨道背景颜色，格式为 `R,G,B,A`，范围 `0-255`。
- `NoteColor`：点击键和长按头的颜色。
- `HoldColor`：长按身体的颜色。
- `JudgementLinePosition`：判定线在轨道区域内的相对位置。`0` 是顶部，`0.5` 是中间，`1` 是底部。
- `KeyCount`：当前使用的键数，合法范围是 `2-7`。
- `[keys:x] LaneTypes`：指定 x 键模式下每个轨道对应空中或地面。`A` 表示空中，`G` 表示地面。
- `[keys:x] Split`：指定 x 键模式下左半区轨道数量，用于 multi 的左右对拍分配。

## 谱面类型映射

| Type | 名称 | 处理方式 |
| --- | --- | --- |
| `1` | `monster` | 普通点击 |
| `2` | `block` | 检查是否会被障碍命中，必要时插入躲避键 |
| `3` | `hold` | 有持续时间的长按 |
| `4` | `ghost` | 按普通点击处理 |
| `5` | `boss` | 点击一次即可，空中或地面都可以 |
| `6` | `energy` | 按人物所在空中/地面位置收集 |
| `7` | `music` | 按人物所在空中/地面位置收集 |
| `8` | `multi` | 根据 BPM 生成连续对拍或重复点击 |

在 multi 持续期间，转换器会忽略其他类型的 note，因为 Muse Dash 规则中 multi 期间不需要单独处理这些对象。

## 编译

项目会引用本地 Muse Dash 目录中由 MelonLoader 生成的程序集：

```xml
<MuseDashPath>D:\APP Profile\steam\steamapps\common\Muse Dash</MuseDashPath>
```

如果 Muse Dash 安装路径不同，可以在编译时传入 `MuseDashPath`：

```powershell
dotnet build "AccuracyIndicator\AccuracyIndicator.csproj" -c Release -p:MuseDashPath="你的 Muse Dash 目录"
```

该目录必须安装 MelonLoader `0.7.3`；项目会在编译前检查 `MelonLoader.dll` 的版本。

编译命令：

```powershell
dotnet build "D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\AccuracyIndicator\AccuracyIndicator.csproj" -c Release
```

DLL 输出路径：

```text
D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\debug\ManiaInMuse.dll
```

## 目录结构

- `AccuracyIndicator/`：主 Mod 源码。命名空间仍保留历史名称，但程序集名和 Mod 名是 `ManiaInMuse`。
- `OsuGenerator/`：独立 CSV 转 osu 的原型工具。
- `DirectOsuPlayer/`：早期用于验证播放器显示效果的浏览器原型。

## 当前版本

`2.1.1`
