using Godot;

/// <summary>
/// 教学提示区：玩家进入时弹出 HUD 提示对话框（LevelHud.ShowHint），离开后淡出收起。
/// - **自包含裸节点**：本体就是感应区，_Ready 里设 layer=0、mask=2（只感应玩家，
///   玩家在物理层 2），场景里没手搭 CollisionShape2D 时按 <see cref="ZoneSize"/> 自动生成一个
///   （手搭了就沿用，可摆任意形状）；
/// - <see cref="Text"/> 写中文原文（经 Tr 翻译），<see cref="Key"/> 是可选按键帽（如 "J"，不翻译）；
/// - <see cref="Once"/>=true 时只在首次进入弹一次（离开再进入不再弹，重载关卡后复位）；
/// - 本体不占物理层、不挡声波、不碰玩家；对话框样式/位置在 关卡HUD.tscn 的 HintBox 上调。
/// </summary>
public partial class HintZone : Area2D {
	/// <summary>提示文本（中文原文，经 Tr 翻译）。</summary>
	[Export] public string Text = "";
	/// <summary>按键帽文字（如 "J" / "空格"）：留空则不显示按键帽；不参与翻译。</summary>
	[Export] public string Key = "";
	/// <summary>只提示一次：首次弹出后，离开再进入不再弹（重载关卡后复位）。</summary>
	[Export] public bool Once = false;
	/// <summary>未手搭碰撞形状时自动生成的感应区尺寸（px）。</summary>
	[Export] public Vector2 ZoneSize = new Vector2(160f, 60f);

	/// <summary>玩家当前是否在区内（防止进入/离开事件重复处理）。</summary>
	private bool _inside;
	/// <summary>本区是否已经弹过提示（配合 Once）。</summary>
	private bool _everShown;

	public override void _Ready() {
		CollisionLayer = 0; // 不占物理层
		CollisionMask = 2;  // 只感应玩家（物理层 2）
		if (!HasOwnShape()) {
			AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = ZoneSize } });
		}
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
		Log.Debug($"提示区[{Name}] 就绪 位置={GlobalPosition.Round()} 尺寸={ZoneSize} Once={Once} 文本={Text}");
	}

	/// <summary>场景里是否已经手搭了碰撞形状（搭了就沿用，支持任意形状/多个形状）。</summary>
	private bool HasOwnShape() {
		foreach (Node child in GetChildren()) {
			if (child is CollisionShape2D || child is CollisionPolygon2D) {
				return true;
			}
		}
		return false;
	}

	private void OnBodyEntered(Node2D body) {
		if (body is not Player || _inside) {
			return;
		}
		_inside = true;
		if (Once && _everShown) {
			return;
		}
		_everShown = true;
		LevelHud.Instance?.ShowHint(this, Tr(Text), Key);
	}

	private void OnBodyExited(Node2D body) {
		if (body is not Player || !_inside) {
			return;
		}
		_inside = false;
		LevelHud.Instance?.ClearHint(this);
	}
}
