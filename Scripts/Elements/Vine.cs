using Godot;
using System.Collections.Generic;

/// <summary>
/// 藤蔓：悬挂在空中的单摆。声波经过时施加角冲量（发声推向远离声源一侧，
/// 回声拉回），玩家可抓住（↑）、沿藤攀爬（W/S）、起跳（空格）。
/// </summary>
public partial class Vine : Node2D {
	[Export] public float Length = 160f;
	[Export] public float Gravity = 900f;
	[Export] public float Damping = 0.6f;
	[Export] public float WaveTorque = 3.2f;
	[Export] public float MaxAngleDeg = 60f;

	private float _theta;
	private float _omega;
	private Area2D _grabArea;
	private WaveField _field;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	/// <summary>藤蔓方向单位向量（从根部指向末端）。</summary>
	private Vector2 Dir => new Vector2(Mathf.Sin(_theta), Mathf.Cos(_theta));

	/// <summary>当前摆角（弧度，供盲视关卡轮廓跟随）。</summary>
	public float Theta => _theta;

	public Vector2 RootPos => GlobalPosition;

	/// <summary>藤蔓上参数 s（0=根部，1=末端）处的世界坐标。</summary>
	public Vector2 GetPointAt(float s) {
		return GlobalPosition + Dir * (Length * s);
	}

	/// <summary>参数 s 处的摆动线速度（用于起跳惯性）。</summary>
	public Vector2 GetVelocityAt(float s) {
		return new Vector2(Mathf.Cos(_theta), -Mathf.Sin(_theta)) * (_omega * Length * s);
	}

	/// <summary>把世界坐标投影到藤蔓参数 s。</summary>
	public float WorldToS(Vector2 worldPos) {
		Vector2 rel = worldPos - GlobalPosition;
		return Mathf.Clamp(rel.Dot(Dir) / Length, 0f, 1f);
	}

	public override void _Ready() {
		_grabArea = GetNode<Area2D>("GrabArea");
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		float maxRad = Mathf.DegToRad(MaxAngleDeg);
		_omega += (-(Gravity / Length) * Mathf.Sin(_theta) - Damping * _omega) * dt;
		_theta += _omega * dt;
		if (Mathf.Abs(_theta) > maxRad) {
			_theta = Mathf.Sign(_theta) * maxRad;
			_omega = 0f;
		}
		if (_field != null) {
			Vector2 center = GlobalPosition + Dir * (Length * 0.5f);
			_buffer.Clear();
			_field.QueryPass(center, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				float push = center.X > ev.Origin.X ? 1f : -1f;
				if (ev.Phase == WaveField.WavePhase.Echo) {
					push = -push;
				}
				_omega += push * WaveTorque * ev.Intensity;
			}
		}
		_grabArea.Position = Dir * (Length * 0.5f);
		_grabArea.Rotation = _theta;
		QueueRedraw();
	}

	public override void _Draw() {
		Vector2 tip = Dir * Length;
		DrawRect(new Rect2(-9, -8, 18, 8), new Color(0.3f, 0.3f, 0.36f));
		DrawRect(new Rect2(-6, -7, 12, 7), new Color(0.5f, 0.38f, 0.22f));
		DrawLine(Vector2.Zero, tip, new Color(0.28f, 0.6f, 0.3f), 3f);
		DrawCircle(tip, 4.5f, new Color(0.55f, 0.9f, 0.5f));
	}
}
