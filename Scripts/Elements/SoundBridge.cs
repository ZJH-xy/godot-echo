using Godot;
using System.Collections.Generic;

/// <summary>
/// 声音桥：声波经过时显现为实体平台（信号保持 HoldTime 秒），保持结束后消失，
/// 消失前快闪预警；回声经过会再次点亮——“回声让桥再次亮起”。
/// 发光期间可站人（物理层 4），不阻挡声波传播。
/// </summary>
public partial class SoundBridge : StaticBody2D {
	[Export] public bool DetectOutbound = true;
	[Export] public bool DetectEcho = true;
	[Export] public float MinIntensity = 0f;
	/// <summary>声波经过后桥维持实体的时长（秒）。</summary>
	[Export] public float HoldTime = 1.6f;
	/// <summary>消失前快闪预警时长（秒）。</summary>
	[Export] public float VanishBlinkTime = 0.4f;
	/// <summary>显现/消失的淡入淡出时长（秒）。</summary>
	[Export] public float FadeTime = 0.25f;

	private WaveField _field;
	private float _holdRemaining;
	private float _fade;
	private bool _solid;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		_fade = 0f;
		Modulate = new Color(1, 1, 1, 0f);
		SetSolid(false);
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				bool ok = (ev.Phase == WaveField.WavePhase.Outbound && DetectOutbound)
						|| (ev.Phase == WaveField.WavePhase.Echo && DetectEcho);
				if (ok && ev.Intensity >= MinIntensity) {
					_holdRemaining = HoldTime;
				}
			}
		}
		_holdRemaining = Mathf.Max(0f, _holdRemaining - dt);
		bool want = _holdRemaining > 0f;
		if (want != _solid) {
			SetSolid(want);
		}
		float target = _solid ? 1f : 0f;
		_fade = Mathf.MoveToward(_fade, target, dt / Mathf.Max(0.01f, FadeTime));
		float alpha = _fade;
		if (_solid && _holdRemaining <= VanishBlinkTime) {
			// 消失前快闪预警：快速明暗交替
			float phase = _holdRemaining / Mathf.Max(0.02f, VanishBlinkTime);
			alpha *= Mathf.PosMod(phase * 5f, 1f) < 0.5f ? 1f : 0.25f;
		}
		Modulate = new Color(1, 1, 1, alpha);
		QueueRedraw();
	}

	private void SetSolid(bool solid) {
		_solid = solid;
		SetDeferred("collision_layer", solid ? 4u : 0u);
	}

	public override void _Draw() {
		var lit = new Color(0.45f, 0.85f, 1f, 0.85f);
		var dim = new Color(0.3f, 0.35f, 0.45f, 0.55f);
		DrawRect(new Rect2(-15, -7, 30, 14), _solid ? lit : dim);
		DrawRect(new Rect2(-15, -7, 30, 14), new Color(0.1f, 0.13f, 0.2f), false, 1f);
		if (_solid) {
			DrawRect(new Rect2(-12, -5, 24, 10), new Color(0.6f, 0.95f, 1f, 0.35f));
		}
	}
}
