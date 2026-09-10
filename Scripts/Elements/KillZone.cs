using Godot;

/// <summary>
/// 危险区：玩家进入后重生（用于水面、深渊）。占位阶段水不可游泳，落水即失败。
/// </summary>
public partial class KillZone : Area2D {
	public override void _Ready() {
		BodyEntered += OnBodyEntered;
	}

	private void OnBodyEntered(Node2D body) {
		if (body is Player player) {
			Log.Debug($"玩家进入危险区[{Name}]，重生");
			player.Respawn();
		}
	}
}
