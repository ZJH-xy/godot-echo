using Godot;
using System.Collections.Generic;

/// <summary>
/// 悬浮方块：悬浮在空中的方块平台。声波把它推离声源（回声弱拉回），
/// 被推离后等待片刻自动“弹簧归位”回到原位；回到原位的瞬间，若玩家正站在方块上，
/// 会对玩家施加向上的弹力（沿归位方向带一点水平推力）。AnimatableBody2D 保证玩家站在上面时被一并带动。
/// </summary>
public partial class FloatBlock : AnimatableBody2D {
	/// <summary>发声波推动强度。</summary>
	[Export] public float PushStrength = 170f;
	/// <summary>速度阻尼：让方块被推动后逐渐停下。</summary>
	[Export] public float Damping = 2.4f;
	/// <summary>被声波推动时的最大速度。</summary>
	[Export] public float MaxSpeed = 190f;
	/// <summary>撞墙后的反弹系数（0~1，越小被墙推回得越猛）。</summary>
	[Export] public float Restitution = 0.45f;
	/// <summary>最后一次被声波推动后，等待多久开始归位（秒）。</summary>
	[Export] public float ReturnDelay = 0.8f;
	/// <summary>归位弹簧加速度（px/s²）。</summary>
	[Export] public float SpringAccel = 1500f;
	/// <summary>归位最大速度。</summary>
	[Export] public float ReturnMaxSpeed = 240f;
	/// <summary>回到原位时对上方玩家施加的向上弹力（px/s）。</summary>
	[Export] public float KickVelocity = 430f;
	/// <summary>回到原位时沿归位方向的水平推力（px/s）。</summary>
	[Export] public float KickHorizontal = 90f;
	/// <summary>视觉上下浮动幅度（仅绘制，不参与物理）。</summary>
	[Export] public float BobAmplitude = 1.5f;
	[Export] public float BobSpeed = 2.4f;
	/// <summary>受击闪光持续时间（秒）：被声波推动时高亮。</summary>
	[Export] public float HitFlashDuration = 0.3f;
	/// <summary>归位弹跳环持续时间（秒）。</summary>
	[Export] public float SnapFlashDuration = 0.4f;

	private const float SnapDist = 2f;

	private Vector2 _home;
	private Vector2 _vel;
	/// <summary>最近一次归位方向（用于归位瞬间对玩家的水平推力）。</summary>
	private Vector2 _returnDir = Vector2.Up;
	private WaveField _field;
	private Area2D _topSensor;
	private float _time;
	/// <summary>距上次被声波推动的时间：超过 ReturnDelay 后开始归位。</summary>
	private float _sincePush;
	private bool _returning;
	private bool _settled = true;
	/// <summary>受击闪光剩余（0~1），驱动高亮与扩散环。</summary>
	private float _hitFlash;
	/// <summary>归位弹跳环剩余（0~1）。</summary>
	private float _snapFlash;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		// 用 MoveAndCollide 实现移动与撞墙反弹，必须先关闭 sync_to_physics
		SyncToPhysics = false;
		// 检测地形墙（物理层 1），碰到后被墙推回
		CollisionMask |= 1;
		_home = GlobalPosition;
		_topSensor = GetNode<Area2D>("TopSensor");
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		_sincePush += dt;
		_hitFlash = Mathf.Max(0f, _hitFlash - dt / Mathf.Max(0.01f, HitFlashDuration));
		_snapFlash = Mathf.Max(0f, _snapFlash - dt / Mathf.Max(0.01f, SnapFlashDuration));
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				Vector2 dir = GlobalPosition - ev.Origin;
				float len = dir.Length();
				dir = len > 1f ? dir / len : Vector2.Right;
				if (ev.Phase == WaveField.WavePhase.Echo) {
					dir = -dir; // 回声向声源收拢（强度已弱于发声）
				}
				_vel += dir * (ev.Intensity * PushStrength);
				_hitFlash = 1f; // 受击闪光：直观看到声波打到了方块
				_sincePush = 0f;
				_returning = false;
				_settled = false;
			}
		}
		Vector2 toHome = _home - GlobalPosition;
		float dist = toHome.Length();
		if (dist > SnapDist) {
			if (!_returning && _sincePush >= ReturnDelay) {
				_returning = true;
			}
			if (_returning) {
				_returnDir = toHome / dist;
				_vel += _returnDir * SpringAccel * dt;
			}
		} else if (!_settled) {
			// 回到原位：吸附归位，并对站在方块上的玩家施加力
			_settled = true;
			_returning = false;
			_vel = Vector2.Zero;
			GlobalPosition = _home;
			_snapFlash = 1f; // 归位弹跳环
			KickPlayerOnTop();
		}
		_vel *= Mathf.Max(0f, 1f - Damping * dt);
		float cap = Mathf.Max(MaxSpeed, ReturnMaxSpeed);
		if (_vel.Length() > cap) {
			_vel = _vel.Normalized() * cap;
		}
		if (_vel.LengthSquared() > 0.01f) {
			var collision = MoveAndCollide(_vel * dt);
			if (collision != null) {
				_vel = _vel.Bounce(collision.GetNormal()) * Restitution;
			}
		}
		GlobalPosition = GlobalPosition.Round();
		QueueRedraw();
	}

	/// <summary>归位瞬间：玩家站在方块上（顶部感应区重叠）时施加弹力。</summary>
	private void KickPlayerOnTop() {
		foreach (var body in _topSensor.GetOverlappingBodies()) {
			if (body is Player player) {
				Vector2 kick = new Vector2(_returnDir.X * KickHorizontal, -KickVelocity);
				player.Velocity = new Vector2(player.Velocity.X + kick.X, Mathf.Min(player.Velocity.Y, kick.Y));
				Log.Debug($"悬浮方块[{Name}] 回到原位，弹起玩家 v={player.Velocity}");
			}
		}
	}

	public override void _Draw() {
		float bob = Mathf.Sin(_time * BobSpeed) * BobAmplitude;
		Color body = new Color(0.55f, 0.62f, 0.78f).Lerp(new Color(0.78f, 0.98f, 1f), _hitFlash * 0.6f);

		// 本体：纯平移，不变形（无拉伸/拖尾/弹簧线）
		DrawRect(new Rect2(-20, -18 + bob, 40, 36), body);
		DrawRect(new Rect2(-20, -18 + bob, 40, 36), new Color(0.22f, 0.25f, 0.36f), false, 1f);
		DrawRect(new Rect2(-14, -14 + bob, 28, 8), new Color(0.72f, 0.8f, 0.94f, 0.5f));

		// 悬浮光晕
		DrawRect(new Rect2(-23, -21 + bob, 46, 42), new Color(0.55f, 0.8f, 1f, 0.16f), false, 1f);

		// 受击扩散环：直观看到声波命中
		if (_hitFlash > 0f) {
			float r = 22f + (1f - _hitFlash) * 26f;
			DrawArc(new Vector2(0, bob), r, 0f, Mathf.Tau, 24, new Color(0.6f, 0.95f, 1f, 0.55f * _hitFlash), 1.5f, false);
		}
		// 归位弹跳环：配合对玩家的弹力
		if (_snapFlash > 0f) {
			float r = 18f + (1f - _snapFlash) * 34f;
			DrawArc(new Vector2(0, bob), r, 0f, Mathf.Tau, 24, new Color(1f, 0.85f, 0.4f, 0.6f * _snapFlash), 2f, false);
		}

		// 原位虚影：被推离时提示归位点（轻微呼吸闪烁）
		if (!_settled) {
			Vector2 ghost = _home - GlobalPosition;
			float ga = 0.16f + 0.08f * Mathf.Sin(_time * 8f);
			DrawRect(new Rect2(ghost.X - 20, ghost.Y - 18, 40, 36), new Color(0.55f, 0.8f, 1f, ga), false, 1f);
		}
	}
}
