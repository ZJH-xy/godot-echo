using Godot;

/// <summary>
/// 设置界面：分辨率（640×360 基准，按屏幕尺寸提供 1x~4x 整数倍档位）、
/// 手柄震动强度（0~1，步长 0.25）、游戏音量（0~1）。
/// 修改即时生效并持久化（user://settings.cfg，见 Core/Settings.cs），返回主菜单。
/// </summary>
public partial class SettingsScreen : Control {
	[Export] private OptionButton _resolutionOption;
	[Export] private HSlider _vibrationSlider;
	[Export] private HSlider _volumeSlider;
	[Export] private Button _backButton;

	public override void _Ready() {
		Log.Init();
		Settings.Load();
		Settings.ClampToScreen(DisplayServer.ScreenGetSize());

		// 分辨率：只列出屏幕放得下的整数倍档位
		Vector2I screen = DisplayServer.ScreenGetSize();
		for (int scale = 1; scale <= 4; scale++) {
			if (Settings.BaseWidth * scale <= screen.X && Settings.BaseHeight * scale <= screen.Y) {
				_resolutionOption.AddItem($"{Settings.BaseWidth * scale}×{Settings.BaseHeight * scale} ({scale}x)", scale - 1);
			}
		}
		if (_resolutionOption.ItemCount == 0) {
			_resolutionOption.AddItem("640×360 (1x)");
		}
		int saved = Mathf.Clamp(Settings.ResolutionScale, 1, _resolutionOption.ItemCount);
		_resolutionOption.Select(saved - 1);

		// 震动：0~1、步长 0.25；音量：0~1
		_vibrationSlider.MinValue = 0;
		_vibrationSlider.MaxValue = 1;
		_vibrationSlider.Step = 0.25f;
		_vibrationSlider.Value = Settings.VibrationStrength;
		_volumeSlider.MinValue = 0;
		_volumeSlider.MaxValue = 1;
		_volumeSlider.Step = 0.05f;
		_volumeSlider.Value = Settings.Volume;

		_resolutionOption.ItemSelected += OnResolutionSelected;
		_vibrationSlider.ValueChanged += OnVibrationChanged;
		_volumeSlider.ValueChanged += OnVolumeChanged;
		_backButton.Pressed += OnBack;
	}

	private void OnResolutionSelected(long index) {
		Settings.ResolutionScale = (int)index + 1;
		Settings.Save();
		Settings.ApplyDisplay(GetWindow());
		Log.Info($"分辨率切换为 {Settings.BaseWidth * Settings.ResolutionScale}×{Settings.BaseHeight * Settings.ResolutionScale}");
	}

	private void OnVibrationChanged(double value) {
		Settings.VibrationStrength = (float)value;
		Settings.Save();
	}

	private void OnVolumeChanged(double value) {
		Settings.Volume = (float)value;
		Settings.Save();
		Settings.ApplyVolume();
	}

	private void OnBack() {
		QueueFree();
	}
}
