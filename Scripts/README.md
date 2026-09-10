# Scripts 目录说明

C# 脚本按职责分目录存放。Godot.NET.Sdk 会自动包含全部子目录下的 `.cs`，移动文件无需修改 csproj；
场景对脚本的引用走 `uid://` 绑定（`.tscn` 中的 path 已同步更新），移动文件不影响运行。

| 目录 | 职责 | 文件 |
|---|---|---|
| `Core/` | 核心玩法系统：玩家控制、声场计算、日志、全局设置、音频 | `Player.cs`、`WaveField.cs`、`Log.cs`、`Settings.cs`、`AudioManager.cs`、`ResourceResolver.cs` |
| `Elements/` | 关卡元素与机关：声波交互物、危险区、提示区、终点 | `SoundDetector.cs`、`EchoDoor.cs`、`FloatBlock.cs`、`MotorPlatform.cs`、`SoundBridge.cs`、`EchoRelay.cs`、`ReflectWall.cs`、`SpringBlock.cs`、`ResonanceDevice.cs`、`SoundWaveRefreshPoint.cs`、`SoundAmplifier.cs`、`Checkpoint.cs`、`Vine.cs`、`KillZone.cs`、`HintZone.cs`、`ExitPortal.cs` |
| `Elements/Scenery/` | 非机关景物：水面/荷叶等自然景物（不属于机关类元素；声波可推动荷叶、荷叶可站人） | `WaterSurface.cs`、`LotusLeaf.cs` |
| `FX/` | 盲视关特效与死亡过渡：浓雾遮挡、地形/物件轮廓、蔚蓝式死亡过渡 | `FogFollow.cs`、`BlindReveal.cs`、`FogOutline.cs`、`DeathTransition.cs` |
| `UI/` | 界面：主菜单、设置、关卡 HUD | `Main.cs`、`SettingsScreen.cs`、`LevelHud.cs` |

维护约定：

- 一个类一个文件，类名与文件名一致；新增脚本后编辑器会生成 `.cs.uid`，需一并提交。
- 各脚本职责与关键机制详见 `Doc/项目简述.md`；物理层、输入、日志等配置在 `project.godot`。
- 本地化用 Godot 内置翻译：英文映射维护在 `Assets/Language/en.po`（中文原文作 msgid），
  场景文本自动翻译，代码里用 `Tr(...)`；语言选择持久化在 `user://settings.cfg` 的 `[i18n]` 段。
- 游戏设置（分辨率/震动/音量）统一读写 `Scripts/Core/Settings.cs`，持久化到 `user://settings.cfg`。
- 音频统一走 `AudioManager`（autoload 节点，`AudioManager.Instance`）：BGM/SE/Voice 三总线定义在
  `default_bus_layout.tres`（**勿删总线**），资源放 `Assets/Audio/{bgm,se,voice}/`，文件名即逻辑名（扩展名可省略）；
  总音量仍由设置面板的“游戏音量”作用于 Master 总线。
- 现有音频是**占位音**（`Assets/Audio/se/*.wav` 12 个音效、`Assets/Audio/bgm/*.ogg` 7 首 BGM，
  均由 `Tools/AudioGen` 离线生成，不进游戏程序集）：要改音色/编曲改 `Tools/AudioGen/Program.cs` 后
  `dotnet run --project Tools/AudioGen` 重新生成（有 ffmpeg 时 BGM 自动转 OGG）；
  要换成真实音频，同名丢进同目录即可（`.ogg` 优先于 `.wav`）。
- **`Assets/` 下的美术与音频一律不入库**（`.gitignore` 忽略整个 `/Assets`）：占位音频靠上面的生成器
  重新生成，克隆仓库后先跑一次生成器；只有代码依赖的文本资源才 `git add -f`（字体主题/翻译/着色器）。
- 关卡 BGM 按**场景文件名**自动选曲（`LevelHud.ResolveBgmName`）：新关卡放 `bgm/<场景名>.ogg` 即生效。
