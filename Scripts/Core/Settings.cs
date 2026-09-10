using Godot;

/// <summary>
/// 全局设置：分辨率倍率（640×360 基准整数倍）、手柄震动强度（0~1）、游戏音量（0~1）。
/// 持久化到 user://settings.cfg（[display]/[controller]/[audio] 段，与 [i18n] 语言共存）；
/// 由主菜单与设置场景加载，声场震动（WaveField）读取 VibrationStrength。
/// </summary>
public static class Settings {
	public const int BaseWidth = 640;
	public const int BaseHeight = 360;
	public const string Path = "user://settings.cfg";

	/// <summary>窗口缩放倍率（1x/2x/3x/4x）。</summary>
	public static int ResolutionScale = 1;
	/// <summary>手柄震动强度 0~1（WaveField 三段式震动按此系数缩放）。</summary>
	public static float VibrationStrength = 1f;
	/// <summary>游戏音量 0~1（作用于 AudioServer 主总线）。</summary>
	public static float Volume = 1f;

	private static bool _loaded;

	/// <summary>从 settings.cfg 读取设置（幂等，可重复调用）。</summary>
	public static void Load() {
		if (_loaded) {
			return;
		}
		_loaded = true;
		var cfg = new ConfigFile();
		if (cfg.Load(Path) != Error.Ok) {
			return;
		}
		ResolutionScale = Mathf.Clamp(cfg.GetValue("display", "scale", 1).AsInt32(), 1, 4);
		VibrationStrength = Mathf.Clamp((float)cfg.GetValue("controller", "vibration", 1.0), 0f, 1f);
		Volume = Mathf.Clamp((float)cfg.GetValue("audio", "volume", 1.0), 0f, 1f);
	}

	/// <summary>写回 settings.cfg；先加载已有文件以保留其他键（如 [i18n] language）。</summary>
	public static void Save() {
		var cfg = new ConfigFile();
		cfg.Load(Path); // 文件不存在时忽略错误，仅写入自己的键
		cfg.SetValue("display", "scale", ResolutionScale);
		cfg.SetValue("controller", "vibration", VibrationStrength);
		cfg.SetValue("audio", "volume", Volume);
		cfg.Save(Path);
	}

	/// <summary>按屏幕尺寸把缩放倍率限制在放得下的最大档位内（防止窗口超出屏幕）。</summary>
	public static void ClampToScreen(Vector2I screen) {
		int max = 1;
		for (int s = 1; s <= 4; s++) {
			if (BaseWidth * s <= screen.X && BaseHeight * s <= screen.Y) {
				max = s;
			}
		}
		ResolutionScale = Mathf.Min(ResolutionScale, max);
	}

	/// <summary>按当前倍率调整窗口大小（视口保持 640×360，配合整数缩放拉伸）。</summary>
	public static void ApplyDisplay(Window window) {
		window.Size = new Vector2I(BaseWidth * ResolutionScale, BaseHeight * ResolutionScale);
	}

	/// <summary>把音量应用到 AudioServer 主总线（0 时静音）。</summary>
	public static void ApplyVolume() {
		int master = AudioServer.GetBusIndex("Master");
		AudioServer.SetBusVolumeDb(master, Volume <= 0.001f ? -60f : Mathf.LinearToDb(Volume));
	}

	public static void ApplyAll(Window window) {
		ApplyDisplay(window);
		ApplyVolume();
	}
}
