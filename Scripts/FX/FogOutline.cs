using Godot;
using System.Collections.Generic;

/// <summary>
/// 盲视关卡物件轮廓组件：挂在藤蔓/检测器/终点等元素下。
/// 声波经过时轮廓亮起并随 RevealDuration 淡出；绘制在浓雾层之上（z_index 高于雾层）。
/// LineMode 沿局部 +Y 画线（配合 FollowVine 跟随藤蔓摆动），否则绘制矩形轮廓。
/// </summary>
public partial class FogOutline : Node2D {
	[Export] public Rect2 OutlineRect = new Rect2(-12, -12, 24, 24);
	[Export] public bool LineMode = false;
	[Export] public Vector2 LineEnd = new Vector2(0, 160);
	[Export] public bool FollowVine = false;
	[Export] public float RevealDuration = 5.5f;
	[Export] public Color Color = new Color(0.75f, 0.95f, 1f);
	[Export] public float LineWidth = 2f;

	private WaveField _field;
	private float _reveal;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		Vine vine = null;
		if (FollowVine && GetParent() is Vine v) {
			vine = v;
		}
		if (_field != null) {
			_buffer.Clear();
			// 藤蔓根部在天花板实心块内，取藤蔓中点（自由空间）查询
			Vector2 queryPos = vine != null ? vine.GetPointAt(0.5f) : GlobalPosition;
			_field.QueryPass(queryPos, GetInstanceId(), _buffer);
			if (_buffer.Count > 0) {
				_reveal = 1f;
			}
		}
		_reveal = Mathf.Max(0f, _reveal - (float)delta / Mathf.Max(0.01f, RevealDuration));
		if (vine != null) {
			Rotation = -vine.Theta;
		}
		QueueRedraw();
	}

	public override void _Draw() {
		if (_reveal <= 0.003f) {
			return;
		}
		var c = Color;
		c.A *= _reveal;
		if (LineMode) {
			DrawLine(Vector2.Zero, LineEnd, c, LineWidth);
		} else {
			DrawRect(OutlineRect, c, false, LineWidth);
		}
	}
}
