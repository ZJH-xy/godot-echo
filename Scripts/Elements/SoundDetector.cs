using Godot;
using System.Collections.Generic;

/// <summary>
/// 声波检测器：声波（发声/回声）经过时发出 Triggered 信号，可连接门等机关。
/// 支持强度阈值与阶段过滤，可继承 OnWavePass/OnWaveDetected 实现不同反应。
/// 两种触发模式（按 EnergyThreshold 区分）：
/// - EnergyThreshold ≤ 0（默认，旧行为）：每道合格声波到达立即触发一次；
/// - EnergyThreshold &gt; 0（累积模式）：把多道声波的能量累加起来（能量随时间衰减），
///   累积达到阈值才触发一次，用于“多次声音才能启动”的机关（例如需要反复发声两次蓄能）。
/// 子类可把 PreferVisualChild 置 false 自行绘制（如共鸣装置）。
/// </summary>
public partial class SoundDetector : Node2D {
	[Export] public bool DetectOutbound = true;
	[Export] public bool DetectEcho = true;
	[Export] public float MinIntensity = 0f;
	[Export] public float HoldTime = 1f;
	[Export] public NodePath VisualPath = new NodePath("Visual");
	[Export] public Color IdleColor = new Color(0.9f, 0.55f, 0.2f);
	[Export] public Color LitColor = new Color(0.3f, 0.9f, 0.4f);
	/// <summary>触发能量阈值：&gt;0 时开启“多次声音累积”模式，多道声波能量累加达标才触发；
	/// ≤0 保持旧行为——每道合格声波立即触发。</summary>
	[Export] public float EnergyThreshold = 0f;
	/// <summary>累积能量的每秒衰减量（仅 EnergyThreshold &gt; 0 时生效）：衰减越快，
	/// 两道声音必须越密集才能凑够能量。</summary>
	[Export] public float EnergyDecay = 0.4f;
	/// <summary>无 Visual 子节点时程序生成的占位方块尺寸。</summary>
	[Export] public Vector2 VisualSize = new Vector2(20f, 20f);

	[Signal] public delegate void TriggeredEventHandler(float intensity);

	protected ColorRect _visual;
	protected WaveField _field;
	/// <summary>触发后的点亮剩余时间（秒），驱动颜色反馈。</summary>
	protected float _litRemaining;
	/// <summary>当前累积能量（仅 EnergyThreshold &gt; 0 时有意义）。</summary>
	protected float _charge;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	/// <summary>子类置 false 时不生成/不使用 Visual 方块，改为自行绘制（需自绘的机关）。</summary>
	protected virtual bool PreferVisualChild => true;

	public override void _Ready() {
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
		if (PreferVisualChild) {
			_visual = GetNodeOrNull<ColorRect>(VisualPath);
			if (_visual == null) {
				// 场景里没摆 Visual 子节点（新摆放的裸检测器）：程序生成一个占位方块
				_visual = new ColorRect {
					Name = "Visual",
					Position = -VisualSize * 0.5f,
					Size = VisualSize,
					Color = IdleColor,
					MouseFilter = Control.MouseFilterEnum.Ignore
				};
				AddChild(_visual);
			}
			_visual.Color = IdleColor;
		} else if (HasNode(VisualPath)) {
			// 允许子类接管绘制；场景自带的 Visual 方块由子类决定如何处理
			_visual = GetNodeOrNull<ColorRect>(VisualPath);
		}
		if (_field != null && _field.IsSolidAt(GlobalPosition)) {
			Log.Warn($"检测器[{Name}] 位于实心单元内，声波无法到达，请调整位置");
		}
	}

	public override void _PhysicsProcess(double delta) {
		if (_field == null) {
			return;
		}
		float dt = (float)delta;
		if (EnergyThreshold > 0f && _charge > 0f) {
			// 能量随时间衰减：攒得太慢就白攒了
			_charge = Mathf.Max(0f, _charge - EnergyDecay * dt);
		}
		_buffer.Clear();
		_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
		foreach (var ev in _buffer) {
			bool ok = (ev.Phase == WaveField.WavePhase.Outbound && DetectOutbound)
					|| (ev.Phase == WaveField.WavePhase.Echo && DetectEcho);
			if (ok && ev.Intensity >= MinIntensity) {
				OnWavePass(ev);
			}
		}
	}

	/// <summary>
	/// 一道合格声波到达：默认按累积模式（EnergyThreshold &gt; 0）累加能量，
	/// 达标后触发一次并清空；否则按旧行为每道声波触发一次。子类可整体重写。
	/// </summary>
	protected virtual void OnWavePass(WaveField.WavePassEvent ev) {
		if (EnergyThreshold > 0f) {
			_charge += ev.Intensity;
			Log.Trace($"检测器[{Name}] 能量 +{ev.Intensity:F2} = {_charge:F2}/{EnergyThreshold:F2}");
			if (_charge >= EnergyThreshold) {
				float fired = _charge;
				_charge = 0f;
				FireTrigger(fired);
			}
		} else {
			FireTrigger(ev.Intensity);
		}
	}

	/// <summary>触发一次：点亮视觉并调用 OnWaveDetected（默认发出 Triggered 信号）。</summary>
	protected void FireTrigger(float intensity) {
		_litRemaining = HoldTime;
		Log.Debug($"检测器[{Name}] 触发 强度={intensity:F2}");
		AudioManager.Instance?.PlaySe("detector", Mathf.Clamp(0.35f + 0.3f * intensity, 0.3f, 0.85f));
		OnWaveDetected(intensity);
	}

	protected virtual void OnWaveDetected(float intensity) {
		EmitSignal(SignalName.Triggered, intensity);
	}

	public override void _Process(double delta) {
		_litRemaining = Mathf.Max(0f, _litRemaining - (float)delta);
		if (_visual != null) {
			if (EnergyThreshold > 0f) {
				// 累积模式：颜色随能量渐亮（到达阈值前就接近触发色）
				float fill = Mathf.Clamp(_charge / Mathf.Max(0.0001f, EnergyThreshold), 0f, 1f);
				_visual.Color = _litRemaining > 0f ? LitColor : IdleColor.Lerp(LitColor, fill * 0.9f);
			} else {
				_visual.Color = _litRemaining > 0f ? LitColor : IdleColor;
			}
		}
	}

	public override void _Draw() {
		// 累积模式：头顶画一根能量条，让玩家知道还差多少能量
		if (EnergyThreshold > 0f && _charge > 0f) {
			DrawRect(new Rect2(-13f, -26f, 26f, 3f), new Color(0f, 0f, 0f, 0.55f));
			float fill = Mathf.Clamp(_charge / Mathf.Max(0.0001f, EnergyThreshold), 0f, 1f);
			DrawRect(new Rect2(-12f, -25f, 24f * fill, 1f), LitColor);
		}
	}

	/// <summary>当前累积能量占阈值的比例（0~1；非累积模式恒为 0）。</summary>
	public float Charge => EnergyThreshold > 0f ? Mathf.Clamp(_charge / EnergyThreshold, 0f, 1f) : 0f;
}
