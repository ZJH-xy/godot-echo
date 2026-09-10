using Godot;
using System.Collections.Generic;

/// <summary>
/// 声控活动平台：沿固定轨道往返移动的机关平台，可载人（AnimatableBody2D）。
/// 两种模式：
/// - Toggle（默认）：每次被触发（检测器信号或声波经过）就换向，停在轨道任一端；
/// - Hold：信号保持期间驶向终点，信号结束后返回起点。
/// 可配置 DetectorPath 连接检测器（与 EchoDoor 相同），未连接时直接响应声波经过。
/// BlocksSound=true 时平台属于地形层，会阻挡声波（按发声时刻的位置计算阻挡）。
/// </summary>
public partial class MotorPlatform : AnimatableBody2D {
	public enum MotorMode {
		Toggle,
		Hold
	}

	/// <summary>相对起点的终点偏移（轨道方向与长度）。</summary>
	[Export] public Vector2 EndOffset = new Vector2(-200f, 0f);
	/// <summary>Toggle：每次触发换向；Hold：信号保持期间驶向终点。</summary>
	[Export] public MotorMode Mode = MotorMode.Toggle;
	/// <summary>轨道移动速度（px/s）。</summary>
	[Export] public float MoveSpeed = 120f;
	/// <summary>Hold 模式下信号保持时长（秒）。</summary>
	[Export] public float HoldTime = 1.2f;
	/// <summary>连接声波检测器（触发信号驱动）；为空则直接响应声波经过。</summary>
	[Export] public NodePath DetectorPath;
	[Export] public bool DetectOutbound = true;
	[Export] public bool DetectEcho = true;
	[Export] public float MinIntensity = 0f;
	/// <summary>true 时平台属于地形层（物理层 1），会阻挡声波传播。</summary>
	[Export] public bool BlocksSound = false;

	private Vector2 _start;
	private Vector2 _end;
	private float _pathLen;
	/// <summary>当前沿轨道的位置（0=起点，1=终点）。</summary>
	private float _t;
	/// <summary>Toggle 模式当前目标（true=终点）。</summary>
	private bool _toEnd;
	/// <summary>Hold 模式信号剩余（秒）。</summary>
	private float _holdRemaining;
	private WaveField _field;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		// 用 MoveAndCollide 实现移动与撞墙保护，必须先关闭 sync_to_physics
		SyncToPhysics = false;
		// 移动时检测地形墙（物理层 1），避免卡进墙里
		CollisionMask |= 1;
		CollisionLayer = BlocksSound ? 1u : 4u;
		_start = GlobalPosition;
		_end = _start + EndOffset;
		_pathLen = Mathf.Max(1f, EndOffset.Length());
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
		if (DetectorPath != null && !DetectorPath.IsEmpty) {
			var detector = GetNodeOrNull<SoundDetector>(DetectorPath);
			if (detector == null) {
				Log.Error($"活动平台[{Name}] 找不到检测器：{DetectorPath}");
			} else {
				detector.Triggered += OnTriggered;
				Log.Debug($"活动平台[{Name}] 已连接检测器 {detector.Name}");
			}
		}
		if (BlocksSound) {
			_field?.MarkSolidDirty();
		}
	}

	private void OnTriggered(float intensity) {
		switch (Mode) {
			case MotorMode.Toggle:
				_toEnd = !_toEnd;
				Log.Debug($"活动平台[{Name}] 换向 → {(_toEnd ? "终点" : "起点")}");
				break;
			case MotorMode.Hold:
				_holdRemaining = HoldTime;
				break;
		}
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		if (_field != null && (DetectorPath == null || DetectorPath.IsEmpty)) {
			// 未连接检测器：直接响应声波经过
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				bool ok = (ev.Phase == WaveField.WavePhase.Outbound && DetectOutbound)
						|| (ev.Phase == WaveField.WavePhase.Echo && DetectEcho);
				if (ok && ev.Intensity >= MinIntensity) {
					OnTriggered(ev.Intensity);
				}
			}
		}
		_holdRemaining = Mathf.Max(0f, _holdRemaining - dt);
		float target = Mode == MotorMode.Hold ? (_holdRemaining > 0f ? 1f : 0f) : (_toEnd ? 1f : 0f);
		if (Mathf.Abs(_t - target) > 0.001f) {
			float step = MoveSpeed * dt / _pathLen;
			_t = Mathf.MoveToward(_t, target, step);
			Vector2 next = _start.Lerp(_end, _t);
			var collision = MoveAndCollide(next - GlobalPosition);
			if (collision != null) {
				// 被地形挡住：停在当前位置，按实际位置回推轨道参数
				_t = Mathf.Clamp((GlobalPosition - _start).Length() / _pathLen, 0f, 1f);
			}
			GlobalPosition = GlobalPosition.Round();
			if (BlocksSound) {
				_field?.MarkSolidDirty();
			}
			QueueRedraw();
		}
	}

	public override void _Draw() {
		// 轨道虚线（按当前节点位置换算回局部坐标）
		Vector2 startLocal = _start - GlobalPosition;
		Vector2 endLocal = _end - GlobalPosition;
		DrawDashedLine(startLocal, endLocal, new Color(0.5f, 0.55f, 0.65f, 0.5f), 2f, 6f);
		// 平台本体
		DrawRect(new Rect2(-24, -6, 48, 12), new Color(0.72f, 0.58f, 0.3f));
		DrawRect(new Rect2(-24, -6, 48, 12), new Color(0.35f, 0.28f, 0.16f), false, 1f);
		DrawRect(new Rect2(-16, -3, 32, 3), new Color(1f, 0.85f, 0.4f, 0.35f));
	}
}
