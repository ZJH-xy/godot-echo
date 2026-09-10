using Godot;

/// <summary>
/// 关卡终点：玩家到达后触发过关结算（HUD 显示完成并返回主菜单）。
/// </summary>
public partial class ExitPortal : Area2D {
	[Export] private int Currentlevel = 0;
	private ColorRect _body;
	private float _t;

	public override void _Ready() {
		BodyEntered += OnBodyEntered;
		_body = GetNode<ColorRect>("Body");
	}

	private void OnBodyEntered(Node2D body) {
		if (body is Player) {
			Log.Info("玩家抵达终点，关卡完成");
			AudioManager.Instance?.PlaySe("complete");
			var cfg = new ConfigFile();
			if (cfg.Load(Main.SETTINGS_PATH) == Error.Ok) {
				cfg.SetValue("archive", "level", Currentlevel + 1);
				cfg.Save(Main.SETTINGS_PATH);
			}
			LevelHud.Instance?.Complete();
		}
	}

	public override void _Process(double delta) {
		_t += (float)delta;
		float a = 0.7f + 0.3f * Mathf.Sin(_t * 3f);
		_body.Modulate = new Color(1f, 1f, 1f, a);
	}
}
