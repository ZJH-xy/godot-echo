using Godot;
using System.Collections.Generic;

/// <summary>
/// 弹力方块（蹦床方块）：固定的弹力平台。玩家从高处踩下或撞到它会被弹开：
/// - 落地/撞击：按“下落速度 × FallRetainFactor（保底 BaseBounceVelocity）”向上（或沿侧面）弹起，
///   低于 ImpactMinSpeed 的轻触不弹（可以正常站上去走路）；
/// - 声波经过：方块“接到声音”后把接触中的玩家弹开，力度 = WaveKickVelocity × 声波强度
///   （站在上面也会被弹起；力度随强度变化）；
/// - 如果踩下的同时正好有声波经过（AmplifyWindow 内），两种弹力叠加并追加 AmplifyBonus——
///   弹性被放大，弹得更高。
/// 摆放：挂本脚本的 StaticBody2D 即可，本体属荷叶层（可站立、不挡声波），
/// 碰撞与感应区都在 _Ready 里按 Size 自动生成，无需手动搭子节点。
/// </summary>
public partial class SpringBlock : StaticBody2D {
	/// <summary>方块碰撞/视觉尺寸（px，占位色块，不参与声波阻挡）。</summary>
	[Export] public Vector2 Size = new Vector2(40f, 24f);
	/// <summary>触发一次“踩踏弹开”所需的最小接近速度（px/s）：低于它视为轻触，可以站在上面。</summary>
	[Export] public float ImpactMinSpeed = 80f;
	/// <summary>顶部落地弹速的保底值（px/s）：即使只是小跳也会被弹到这个速度。</summary>
	[Export] public float BaseBounceVelocity = 340f;
	/// <summary>高处落下时：弹速 = max(保底, 下落速度 × 该系数)，从越高处踩下弹得越高。</summary>
	[Export] public float FallRetainFactor = 1.05f;
	/// <summary>侧面撞击弹开的保底速度（px/s）。</summary>
	[Export] public float SideBounceVelocity = 200f;
	/// <summary>侧面撞击时：弹速 = max(保底, 撞击速度 × 该系数)。</summary>
	[Export] public float SideRetainFactor = 0.75f;
	/// <summary>声波弹开系数：弹速增量 = 该值 × 声波强度（强度 0.35~1）。</summary>
	[Export] public float WaveKickVelocity = 620f;
	/// <summary>“踩踏 + 声波同刻”额外追加的弹速（px/s），放大弹性弹得更高。</summary>
	[Export] public float AmplifyBonus = 240f;
	/// <summary>声波经过后多久内落地都算“同时”（秒），享受叠加放大。</summary>
	[Export] public float AmplifyWindow = 0.12f;
	/// <summary>单次弹速上限（px/s），防止叠加后数值失控。</summary>
	[Export] public float MaxKickVelocity = 1150f;

	// 感应区相对方块四周的扩展：上方留出“站在顶面”的重叠带，四周留碰撞余量
	private const float SensorTopExtra = 8f;
	private const float SensorBottomExtra = 4f;
	private const float SensorSideExtra = 3f;
	private const float KickFlashDuration = 0.35f;
	private const float WaveFlashDuration = 0.4f;

	private WaveField _field;
	private Area2D _sensor;
	private float _time;
	/// <summary>最近一次弹开玩家后的时间（秒），防止同帧/连续帧重复触发。</summary>
	private float _kickedAt = -1f;
	/// <summary>最近一次声波到达的时间（秒）与强度：踩踏与声波“同时”的判定窗口。</summary>
	private float _lastWaveAt = -1e9f;
	private float _lastWaveIntensity;
	/// <summary>弹开脉冲（0~1，驱动压缩/扩散环）。</summary>
	private float _kickFlash;
	/// <summary>接到声波的闪光（0~1，驱动高亮/声波环）。</summary>
	private float _waveFlash;
	private readonly List<WaveField.WavePassEvent> _buffer = new();

	public override void _Ready() {
		// 荷叶层（3）：玩家可站立（mask 5 含层 3），但不挡声波（声波只被层 1 挡）
		CollisionLayer = 4u;
		CollisionMask = 0u;
		var bodyShape = new CollisionShape2D {
			Name = "Shape",
			Shape = new RectangleShape2D { Size = Size }
		};
		AddChild(bodyShape);
		// 感应区：比本体四周略大，顶部多留一段“站人重叠带”
		_sensor = new Area2D {
			Name = "TouchSensor",
			CollisionLayer = 0u,
			CollisionMask = 2u // 只感应玩家（物理层 2）
		};
		var sensorShape = new CollisionShape2D {
			Shape = new RectangleShape2D {
				Size = new Vector2(Size.X + SensorSideExtra * 2f, Size.Y + SensorTopExtra + SensorBottomExtra)
			}
		};
		_sensor.Position = new Vector2(0f, (SensorBottomExtra - SensorTopExtra) * 0.5f);
		_sensor.AddChild(sensorShape);
		AddChild(_sensor);
		_sensor.BodyEntered += OnBodyEntered;
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		_kickFlash = Mathf.Max(0f, _kickFlash - dt / KickFlashDuration);
		_waveFlash = Mathf.Max(0f, _waveFlash - dt / WaveFlashDuration);
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition, GetInstanceId(), _buffer);
			if (_buffer.Count > 0) {
				// 取本帧最强的一道声波
				float wave = 0f;
				foreach (var ev in _buffer) {
					_lastWaveAt = _time;
					_lastWaveIntensity = Mathf.Max(_lastWaveIntensity, ev.Intensity);
					wave = Mathf.Max(wave, ev.Intensity);
				}
				_waveFlash = 1f;
				// 声波到达时若玩家正接触方块（站在上面/正要落下），把玩家弹开
				var player = GetTouchingPlayer();
				if (player != null && _time - _kickedAt > 0.05f) {
					LaunchPlayer(player, wave);
				}
			}
		}
		QueueRedraw();
	}

	private void OnBodyEntered(Node2D body) {
		if (body is not Player player || _time - _kickedAt < 0.06f) {
			return;
		}
		// 落地/撞击瞬间：若声波刚过（AmplifyWindow 内）则弹力叠加放大
		float wave = _time - _lastWaveAt <= AmplifyWindow ? _lastWaveIntensity : 0f;
		LaunchPlayer(player, wave);
	}

	/// <summary>当前与感应区重叠的玩家（没有则为 null）。</summary>
	private Player GetTouchingPlayer() {
		foreach (var body in _sensor.GetOverlappingBodies()) {
			if (body is Player player) {
				return player;
			}
		}
		return null;
	}

	/// <summary>
	/// 尝试把玩家弹开：接近速度达标（踩踏/撞击）或带声波能量时施加弹力，
	/// 两者同时满足则叠加并追加 AmplifyBonus。弹速方向 = 玩家相对方块所在面的法线。
	/// </summary>
	private bool LaunchPlayer(Player player, float wave) {
		Vector2 n = FaceNormalToward(player);
		float a = Mathf.Max(0f, -player.Velocity.Dot(n)); // 相对方块面的接近速度
		bool impact = a >= ImpactMinSpeed;
		if (!impact && wave <= 0f) {
			return false;
		}
		float kick = 0f;
		if (impact) {
			// 顶部踩踏用下落速度放大；侧面撞击按水平速度反弹
			bool top = n.Y < -0.5f;
			float baseV = top ? BaseBounceVelocity : SideBounceVelocity;
			float retain = top ? FallRetainFactor : SideRetainFactor;
			kick = Mathf.Max(baseV, a * retain);
		}
		if (wave > 0f) {
			kick += WaveKickVelocity * wave;
			if (impact) {
				kick += AmplifyBonus; // 踩下 + 声波同刻：弹性放大
			}
		}
		kick = Mathf.Min(kick, MaxKickVelocity);
		// 沿法线把接近速度反射为弹出速度（保留切向速度），方向由 n 决定（顶面向上/侧面推开）
		player.Velocity += n * (a + kick);
		_kickFlash = 1f;
		_kickedAt = _time;
		// 弹力音：弹得越猛音越高（kick 归一化到 MaxKickVelocity）
		AudioManager.Instance?.PlaySe("bounce", 0.5f + 0.3f * Mathf.Clamp(kick / MaxKickVelocity, 0f, 1f),
			Mathf.Lerp(0.85f, 1.35f, Mathf.Clamp(kick / MaxKickVelocity, 0f, 1f)));
		Log.Debug($"弹力方块[{Name}] 弹开玩家 方向={n} 弹速={kick:F0} 接近={a:F0} 声波={wave:F2}");
		return true;
	}

	/// <summary>玩家相对方块所在的面：顶面/底面（水平方向重叠时）或左右侧面。</summary>
	private Vector2 FaceNormalToward(Player player) {
		Vector2 rel = player.GlobalPosition - GlobalPosition;
		float hw = Size.X * 0.5f;
		float hh = Size.Y * 0.5f;
		// 只有玩家的身体大致在本体正上方/正下方才算顶/底面接触（避免“从旁边经过”被误判）
		float over = Mathf.Max(1f, hw - 4f);
		if (rel.Y < -hh * 0.4f && Mathf.Abs(rel.X) <= over) {
			return new Vector2(0f, -1f);
		}
		if (rel.Y > hh * 0.4f && Mathf.Abs(rel.X) <= over) {
			return new Vector2(0f, 1f);
		}
		float sx = rel.X >= 0f ? 1f : -1f;
		if (Mathf.Abs(rel.X) < 2f) {
			// 正侧面：往玩家来的方向推
			sx = -Mathf.Sign(player.Velocity.X);
			if (sx == 0f) {
				sx = 1f;
			}
		}
		return new Vector2(sx, 0f);
	}

	public override void _Draw() {
		float hw = Size.X * 0.5f;
		float hh = Size.Y * 0.5f;
		float k = _kickFlash;
		float w = _waveFlash;
		Color body = new Color(0.5f, 0.46f, 0.4f);
		Color border = new Color(0.24f, 0.21f, 0.2f);
		Color plate = new Color(1f, 0.78f, 0.3f).Lerp(new Color(0.65f, 0.95f, 1f), w * 0.55f);
		Color spring = new Color(0.72f, 0.58f, 0.36f);

		// 本体（占位方块，纯平移）
		DrawRect(new Rect2(-hw, -hh, Size.X, Size.Y), body);
		DrawRect(new Rect2(-hw, -hh, Size.X, Size.Y), border, false, 1f);
		DrawRect(new Rect2(-hw + 2f, -hh + 2f, Size.X - 4f, Mathf.Max(1f, Size.Y - 6f)), new Color(0.62f, 0.58f, 0.52f, 0.55f));

		// 顶部弹簧板：弹开瞬间往下压一拍（仅视觉）
		float lift = k > 0f ? 2.5f * k : 0f;
		DrawRect(new Rect2(-hw + 2f, -hh + 2f + lift, Size.X - 4f, 5f), plate);
		DrawRect(new Rect2(-hw + 2f, -hh + 2f + lift, Size.X - 4f, 5f), new Color(0.55f, 0.35f, 0.1f, 0.7f), false, 1f);

		// 弹簧示意线（两侧竖折线）
		float coilY = -hh + 9f + lift;
		for (int side = -1; side <= 1; side += 2) {
			float cx = side * Mathf.Max(4f, hw * 0.35f);
			float midY = -hh * 0.35f;
			var pts = new Vector2[] {
				new Vector2(cx, coilY), new Vector2(cx - 2f, (coilY + midY) * 0.5f),
				new Vector2(cx + 2f, (coilY + midY) * 0.5f + 1f), new Vector2(cx, midY)
			};
			DrawPolyline(pts, spring, 1f, true);
		}

		// 声波命中环（青色）
		if (w > 0f) {
			float r = hw + 4f + (1f - w) * 18f;
			DrawArc(new Vector2(0f, 0f), r, 0f, Mathf.Tau, 24, new Color(0.6f, 0.95f, 1f, 0.6f * w), 1.5f, false);
		}
		// 弹开脉冲环（琥珀色）
		if (k > 0f) {
			float r = Mathf.Max(hw, hh) + 6f + (1f - k) * 26f;
			DrawArc(new Vector2(0f, 0f), r, 0f, Mathf.Tau, 24, new Color(1f, 0.85f, 0.4f, 0.65f * k), 2f, false);
		}
	}
}
