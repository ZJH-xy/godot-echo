using Godot;
using System.Collections.Generic;

/// <summary>
/// 共鸣装置：要求两路声音几乎“同时”到达（到达时刻差 ≤ SimultaneousWindow）才触发——
/// 单路声音经过只会记录，不会触发。典型谜题是让初始声波与回声同刻抵达：
/// 例如与回声中继器配合，把一道转发声波的到达时刻对准原声波回声的到达时刻，
/// 两路声音在此重合即产生共鸣（也可用两次发声让第二道声波赶上第一道的回声）。
/// 继承自 SoundDetector：可直接被 EchoDoor / MotorPlatform 的 DetectorPath 引用
/// （同一 Triggered 信号），阶段过滤（DetectOutbound/DetectEcho）与 MinIntensity 同样生效。
/// 摆放：挂本脚本的 Node2D 即可，视觉完全自绘，无需 Visual 子节点。
/// </summary>
public partial class ResonanceDevice : SoundDetector {
	/// <summary>两路声波到达时刻差不超过该值（秒）即视为“同时到达”。</summary>
	[Export] public float SimultaneousWindow = 0.2f;
	/// <summary>触发脉冲的持续时长（秒），驱动扩散环反馈。</summary>
	[Export] public float PulseDuration = 0.45f;

	private const float SoundRingRadius = 7f;

	/// <summary>已到达、尚未配成对的声音（按到达时刻先后）。</summary>
	private readonly List<WaveField.WavePassEvent> _pending = new();
	/// <summary>共鸣触发脉冲（0~1）。</summary>
	private float _pulse;
	/// <summary>呼吸动画计时。</summary>
	private float _animTime;

	// 本装置完全自绘（双环图标），不用基类的占位方块
	protected override bool PreferVisualChild => false;

	public override void _Ready() {
		base._Ready();
		if (_visual != null) {
			// 场景里残留的 Visual 方块会盖住自绘图标，一律隐藏（本类不需要它）
			_visual.Visible = false;
			_visual = null;
		}
	}

	protected override void OnWavePass(WaveField.WavePassEvent ev) {
		float winMs = Mathf.Max(0f, SimultaneousWindow) * 1000f;
		// 与这路声音相差太远的记录永远不可能配对成“同时”，先清掉
		for (int i = _pending.Count - 1; i >= 0; i--) {
			if (ev.SimTimeMs - _pending[i].SimTimeMs > winMs) {
				_pending.RemoveAt(i);
			}
		}
		foreach (var p in _pending) {
			if (Mathf.Abs(ev.SimTimeMs - p.SimTimeMs) <= winMs) {
				// 两路声音同刻到达 → 共鸣触发；清空记录，防止同一对声音重复触发
				_pending.Clear();
				_pulse = 1f;
				float sum = Mathf.Clamp(ev.Intensity + p.Intensity, 0f, 1f);
				Log.Debug($"共鸣装置[{Name}] 两路声波同刻到达（相差 {(ev.SimTimeMs - p.SimTimeMs) * 0.001f:F3}s），触发 强度={sum:F2}");
				FireTrigger(sum);
				return;
			}
		}
		// 只来了一路：先记下，等另一路
		_pending.Add(ev);
	}

	public override void _Process(double delta) {
		base._Process(delta); // 基类负责 _litRemaining 衰减等
		float dt = (float)delta;
		_pulse = Mathf.Max(0f, _pulse - dt / Mathf.Max(0.01f, PulseDuration));
		_animTime += dt;
		QueueRedraw();
	}

	public override void _Draw() {
		bool lit = _litRemaining > 0f;
		// 两个重叠的圆环 = 两路声音在中心汇合；随到达一路/触发而变亮
		Color ring = lit ? LitColor : IdleColor.Lerp(LitColor, Mathf.Min(1f, _pending.Count * 0.5f));
		ring.A = lit ? 1f : 0.55f + 0.25f * Mathf.Sin(_animTime * 3f);
		DrawArc(new Vector2(-4f, 0f), SoundRingRadius, 0f, Mathf.Tau, 20, ring, 1.5f, false);
		DrawArc(new Vector2(4f, 0f), SoundRingRadius, 0f, Mathf.Tau, 20, ring, 1.5f, false);
		// 中心：空闲暗点；已到达一路声音 → 半亮
		Color core = IdleColor.Darkened(0.45f);
		if (_pending.Count > 0) {
			core = IdleColor.Lerp(LitColor, 0.5f);
		}
		if (lit) {
			core = LitColor;
		}
		DrawCircle(Vector2.Zero, 2.2f, core);
		// 已记录一路声音的小提示点（等第二路）
		if (_pending.Count > 0) {
			DrawCircle(new Vector2(0f, -14f), 1.6f, IdleColor.Lerp(LitColor, 0.6f));
		}
		// 共鸣触发：扩散脉冲环
		if (_pulse > 0f) {
			float r = 10f + (1f - _pulse) * 26f;
			DrawArc(Vector2.Zero, r, 0f, Mathf.Tau, 24, new Color(LitColor, 0.7f * _pulse), 2f, false);
		}
	}
}
