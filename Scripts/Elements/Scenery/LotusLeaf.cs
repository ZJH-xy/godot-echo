using Godot;
using System.Collections.Generic;

/// <summary>
/// 荷叶：漂浮在水面上的移动平台。
/// 发声波把荷叶推离声源，回声把荷叶拉回声源——但回声强度（WaveField.EchoIntensityFactor=0.5）
/// 小于发声，所以荷叶只会被拉回一部分，不会像以前那样固定返回原位。
/// 无回位弹簧：被推开后停在位移处（水阻力使其停下），更符合现实物理。
/// 碰到地形墙或水面边界会被推回（速度反弹）。AnimatableBody2D 保证玩家站在上面时被一并带动。
/// </summary>
public partial class LotusLeaf : AnimatableBody2D {
	[Export] public float PushStrength = 180f;
	/// <summary>水阻力系数：速度每帧按 (1 - Damping*dt) 衰减，让荷叶自然停下。</summary>
	[Export] public float Damping = 1.8f;
	[Export] public float MaxSpeed = 170f;
	/// <summary>撞墙/触边后的反弹系数（0~1，越小被墙推回得越猛）。</summary>
	[Export] public float Restitution = 0.3f;
	[Export] public Rect2 WaterBounds = new Rect2(440, 306, 224, 12);
	[Export] public float BobAmplitude = 2.5f;
	[Export] public float BobSpeed = 2.2f;

	private Vector2 _vel;
	private WaveField _field;
	private float _time;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		// 用 MoveAndCollide 实现撞墙反弹，必须先关闭 sync_to_physics
		// （AnimatableBody2D 默认开启，与移动函数冲突会报
		//  “Move functions do not work together with 'sync to physics' option”）
		SyncToPhysics = false;
		// 让荷叶能检测到地形墙（物理层 1），碰到后被墙推回
		CollisionMask |= 1;
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				Vector2 dir = GlobalPosition - ev.Origin;
				float len = dir.Length();
				dir = len > 1f ? dir / len : Vector2.Right;
				if (ev.Phase == WaveField.WavePhase.Echo) {
					dir = -dir; // 回声向声源收拢（强度已弱于发声，故拉回距离更小）
				}
				_vel += dir * (ev.Intensity * PushStrength);
			}
		}
		// 水阻力：无回位弹簧——被推开后停在位移处，不再强制回到原位
		_vel *= Mathf.Max(0f, 1f - Damping * dt);
		if (_vel.Length() > MaxSpeed) {
			_vel = _vel.Normalized() * MaxSpeed;
		}
		// 移动并检测地形墙：碰到墙壁后被墙推回（速度反弹）
		var collision = MoveAndCollide(_vel * dt);
		if (collision != null) {
			_vel = _vel.Bounce(collision.GetNormal()) * Restitution;
		}
		// 水面边界视作墙：触边反弹并夹紧位置
		float hw = 20f;
		float hh = 6f;
		float minX = WaterBounds.Position.X + hw;
		float maxX = WaterBounds.End.X - hw;
		float minY = WaterBounds.Position.Y + hh;
		float maxY = WaterBounds.End.Y - hh;
		Vector2 pos = GlobalPosition;
		if (pos.X <= minX && _vel.X < 0f) {
			_vel.X = -_vel.X * Restitution;
		} else if (pos.X >= maxX && _vel.X > 0f) {
			_vel.X = -_vel.X * Restitution;
		}
		if (pos.Y <= minY && _vel.Y < 0f) {
			_vel.Y = -_vel.Y * Restitution;
		} else if (pos.Y >= maxY && _vel.Y > 0f) {
			_vel.Y = -_vel.Y * Restitution;
		}
		pos.X = Mathf.Clamp(pos.X, minX, maxX);
		pos.Y = Mathf.Clamp(pos.Y, minY, maxY);
		GlobalPosition = pos.Round();
		QueueRedraw();
	}

	public override void _Draw() {
		float bob = Mathf.Sin(_time * BobSpeed) * BobAmplitude;
		var green = new Color(0.32f, 0.72f, 0.38f);
		DrawCircle(new Vector2(-16, bob), 6, green);
		DrawCircle(new Vector2(16, bob), 6, green);
		DrawRect(new Rect2(-20, -6 + bob, 40, 12), green);
		DrawRect(new Rect2(-20, -6 + bob, 40, 12), new Color(0.2f, 0.5f, 0.26f), false, 1f);
	}
}
