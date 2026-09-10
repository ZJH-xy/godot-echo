using Godot;

/// <summary>
/// 反射墙：像镜面一样反弹声波的墙体。
/// - 扇形声波照镜面法则反射（入射角 = 反射角，由墙面法线与接收角度决定），可多次反射；
/// - 全向声波不反射，遇到反射墙同普通地形一样止步（阻挡传播）；
/// - 本体属物理层 1（地形层）：会挡住玩家，也计入声场实心网格（挡住全向波与网格步进）。
/// 摆放：直接放一个挂本脚本的 StaticBody2D，用节点旋转控制朝向（水平/竖直/斜墙），
/// 墙体会在 _Ready 里按 Length/Thickness 自动生成碰撞与占位绘制，无需手动搭子节点。
/// </summary>
public partial class ReflectWall : StaticBody2D {
	/// <summary>墙面长度（px，沿本体局部 X 轴）。</summary>
	[Export] public float Length = 120f;
	/// <summary>墙厚（px，沿本体局部 Y 轴）。</summary>
	[Export] public float Thickness = 8f;

	private RectangleShape2D _rectShape;

	public override void _Ready() {
		// 墙不动，无需响应别的物体的碰撞；保留地形层（1）用于挡玩家/挡声波
		CollisionLayer = 1u;
		CollisionMask = 0u;
		// 声场用组识别“可反射”的墙
		AddToGroup("reflect_wall");
		_rectShape = new RectangleShape2D { Size = new Vector2(Length, Thickness) };
		var shape = new CollisionShape2D {
			Name = "Shape",
			Shape = _rectShape
		};
		AddChild(shape);
		// 墙体注册可能晚于声场首帧预计算：让声场下次发声前把墙体计入实心格
		Callable.From(() => (GetTree().GetFirstNodeInGroup("wave_field") as WaveField)?.MarkSolidDirty())
			.CallDeferred();
	}

	public override void _Draw() {
		float hw = Length * 0.5f;
		float hh = Thickness * 0.5f;
		// 镜面：浅蓝面 + 深色描边 + 高光条（像素风占位）
		DrawRect(new Rect2(-hw, -hh, Length, Thickness), new Color(0.55f, 0.78f, 0.95f, 0.92f));
		DrawRect(new Rect2(-hw, -hh, Length, Thickness), new Color(0.16f, 0.3f, 0.45f), false, 1f);
		DrawRect(new Rect2(-hw + 2f, -hh + 2f, Length - 4f, Mathf.Max(1f, Thickness - 4f)), new Color(0.85f, 0.97f, 1f, 0.55f));
		// 中缝线：提示这是“会反射”的特殊墙面
		if (Thickness >= 8f) {
			DrawRect(new Rect2(-hw, -0.5f, Length, 1f), new Color(0.2f, 0.45f, 0.7f, 0.6f));
		}
	}
}
