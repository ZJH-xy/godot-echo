using Godot;
using System.Collections.Generic;

/// <summary>
/// 声波放大器：把经过的声波“成倍放大”。
/// - 声波射线穿过本区域时，其后路径的强度 × AmplifyFactor（每穿过一次放大一次，
///   同一道光波来回穿过多段会依次累积——与反射墙组合可做多段放大）；
/// - 放大后的强度可以超过玩家普通发声的 1.0 上限；强度大于 1.0 的声波经过玩家时会推动玩家
///   （推力 = (强度 − 1) × Player.WavePushVelocity，随强度增大），
///   典型用法：将放大后的声波经反射墙弹回玩家把玩家推动；
/// - 纯区域效果：不遮挡声波（不属地形层）、不碰撞玩家，回声不受放大器影响（回声无射线路径）；
/// - 声波经过时本体发光提示；放大器为静态区域，随声场实心网格一起预计算。
/// 摆放：直接放一个挂本脚本的 Node2D 即可，Size 即放大区域（轴对齐矩形），无需搭子节点。
/// </summary>
public partial class SoundAmplifier : Node2D {
	/// <summary>放大区域尺寸（px，轴对齐矩形，居中于节点位置）。</summary>
	[Export] public Vector2 Size = new Vector2(24f, 64f);
	/// <summary>声波每穿过一次区域时的强度倍率（>1 生效；例如 2 = 强度翻倍）。</summary>
	[Export] public float AmplifyFactor = 2f;

	private static readonly Color CoreColor = new(0.62f, 0.95f, 1f);

	private WaveField _field;
	private float _time;
	/// <summary>声波经过的闪光（0~1）。</summary>
	private float _flash;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		AddToGroup("wave_amplifier");
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	/// <summary>放大区域的世界矩形（轴对齐），供声场把覆盖单元标为放大区。</summary>
	public Rect2 WorldRect => new Rect2(GlobalPosition - Size * 0.5f, Size);

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		_flash = Mathf.Max(0f, _flash - dt / 0.45f);
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			if (_buffer.Count > 0) {
				// 声波（含放大后的声波）经过：闪烁提示
				_flash = 1f;
			}
		}
		QueueRedraw();
	}

	public override void _Draw() {
		float hw = Size.X * 0.5f;
		float hh = Size.Y * 0.5f;
		float k = _flash;
		// 区域底色 + 描边（放大时描边变亮）
		DrawRect(new Rect2(-hw, -hh, Size.X, Size.Y), new Color(0.1f, 0.16f, 0.22f, 0.35f));
		Color border = new Color(0.26f, 0.42f, 0.52f).Lerp(Colors.White, k * 0.6f);
		DrawRect(new Rect2(-hw, -hh, Size.X, Size.Y), border, false, 1f);
		// 中心亮柱：声波核心在此被放大的意象
		float barW = Mathf.Min(6f, hw * 0.6f);
		DrawRect(new Rect2(-barW * 0.5f, -hh + 6f, barW, Mathf.Max(1f, Size.Y - 12f)), new Color(CoreColor, 0.28f + 0.5f * k));
		// 双侧内聚箭头（左右各 3 个，尖朝中轴）：经过的声波被“聚焦放大”
		for (int i = 0; i < 3; i++) {
			float y = Mathf.Lerp(-hh + 12f, hh - 12f, i / 2f);
			float a = 0.5f + 0.4f * k;
			DrawColoredPolygon(new[] {
				new Vector2(-hw + 4f, y - 4f), new Vector2(-hw + 10f, y), new Vector2(-hw + 4f, y + 4f)
			}, new Color(CoreColor, a));
			DrawColoredPolygon(new[] {
				new Vector2(hw - 4f, y - 4f), new Vector2(hw - 10f, y), new Vector2(hw - 4f, y + 4f)
			}, new Color(CoreColor, a));
		}
		// 声波经过时的脉冲环
		if (k > 0f) {
			float r = Mathf.Max(hw, hh) + 4f + (1f - k) * 18f;
			DrawArc(Vector2.Zero, r, 0f, Mathf.Tau, 24, new Color(0.6f, 0.95f, 1f, 0.55f * k), 1.5f, false);
		}
	}
}
