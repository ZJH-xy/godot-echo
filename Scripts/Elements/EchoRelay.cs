using Godot;
using System.Collections.Generic;

/// <summary>
/// 回声中继器：声波（发声/回声，可配置）经过时，从自身位置再发射一道声波——
/// 把声音中继/放大到原本够不到的地方。默认发射定向扇形（不产生回声，单发可控）；
/// OmniMode=true 时发射全向声波（会产生回声，可能形成自续共振——注意设计关卡时控制连锁）。
/// </summary>
public partial class EchoRelay : Node2D {
	[Export] public bool ListenOutbound = true;
	[Export] public bool ListenEcho = true;
	[Export] public float MinIntensity = 0f;
	/// <summary>两次中继的最小间隔（秒），防止连锁触发。</summary>
	[Export] public float MinInterval = 1.0f;
	/// <summary>发射强度 = 入射强度 × 该倍率（上限 1）。</summary>
	[Export] public float IntensityBoost = 1.25f;
	/// <summary>true=发射全向声波（会产生回声，可自续共振）；false=发射定向扇形（无回声）。</summary>
	[Export] public bool OmniMode = false;
	[Export] public Vector2 FanDir = Vector2.Left;
	[Export] public float FanHalfAngleDeg = 30f;

	private WaveField _field;
	private float _intervalRemaining;
	/// <summary>中继闪光剩余（秒），用于绘制脉冲。</summary>
	private float _flash;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_intervalRemaining = Mathf.Max(0f, _intervalRemaining - dt);
		_flash = Mathf.Max(0f, _flash - dt);
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				bool ok = (ev.Phase == WaveField.WavePhase.Outbound && ListenOutbound)
						|| (ev.Phase == WaveField.WavePhase.Echo && ListenEcho);
				if (ok && ev.Intensity >= MinIntensity && _intervalRemaining <= 0f) {
					Relay(ev.Intensity);
					_intervalRemaining = MinInterval;
				}
			}
		}
		QueueRedraw();
	}

	private void Relay(float intensity) {
		float strength = Mathf.Clamp(intensity * IntensityBoost, 0.05f, 1f);
		// 转发音：比玩家发声更闷更轻（低音量 + 低音高），一听就知道是“中继”来的
		AudioManager.Instance?.PlaySe(OmniMode ? "wave_omni" : "wave_fan", 0.3f + 0.3f * strength, 0.8f);
		if (OmniMode) {
			Log.Debug($"中继器[{Name}] 再发射全向声波 强度={strength:F2}");
			_field?.EmitOmni(GlobalPosition, strength);
		} else {
			Log.Debug($"中继器[{Name}] 再发射扇形声波 强度={strength:F2} 方向={FanDir}");
			_field?.EmitFan(GlobalPosition, FanDir.Normalized(), FanHalfAngleDeg, strength);
		}
		_flash = 0.3f;
	}

	public override void _Draw() {
		var crystal = new Color(0.65f, 0.9f, 1f);
		float glow = _flash > 0f ? 1f - _flash / 0.3f : 0f;
		var pts = new Vector2[] {
			new Vector2(0, -10), new Vector2(7, 0), new Vector2(0, 10), new Vector2(-7, 0)
		};
		DrawColoredPolygon(pts, crystal.Darkened(0.35f));
		DrawPolyline(pts, new Color(0.2f, 0.35f, 0.5f), 1f);
		if (glow > 0f) {
			DrawCircle(Vector2.Zero, 12f + 8f * glow, new Color(0.6f, 0.95f, 1f, 0.4f * glow));
		}
		if (!OmniMode) {
			Vector2 dir = FanDir.Normalized();
			DrawLine(Vector2.Zero, dir * 16f, new Color(0.6f, 0.95f, 1f, 0.6f), 1f);
		}
	}
}
