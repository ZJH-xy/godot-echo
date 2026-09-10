using Godot;

/// <summary>
/// 声波刷新点：拾取物刷新站。放置后会在自身位置生成一个可被玩家拾取的“技能点”：
/// - 玩家身体进入感应区即拾取：立即刷新声波冷却（清空发声冷却，并重置本次空中的
///   子弹时间 1 次 / 声波 1 次限制，使玩家在空中也能立即再次触发子弹时间、释放声波）；
/// - 拾取后技能点消失，等待 RespawnTime 后重新生成；
/// - 玩家必须离开感应区后再进入才能再次拾取（防止站在点上原地反复拾取）。
/// 摆放：直接放一个挂本脚本的节点（Node2D）即可，感应区与占位绘制在 _Ready 自动生成，
/// 本体不占物理层、不挡声波；可在一关内摆多个。
/// </summary>
public partial class SoundWaveRefreshPoint : Node2D {
	/// <summary>拾取后技能点重新生成的等待时间（秒）。</summary>
	[Export] public float RespawnTime = 3f;
	/// <summary>拾取感应半径（px）：玩家身体进入该范围即拾取。</summary>
	[Export] public float PickupRadius = 16f;

	private Area2D _sensor;
	private float _time;
	/// <summary>技能点未生成时距重新生成的剩余时间（秒）。</summary>
	private float _respawnLeft;
	/// <summary>技能点当前是否已生成（可拾取）。</summary>
	private bool _active = true;
	/// <summary>拾取后玩家尚未离开感应区：禁止再次拾取（重新生成后也须先离开再进入）。</summary>
	private bool _needExit;

	public override void _Ready() {
		_sensor = new Area2D {
			Name = "PickupSensor",
			CollisionLayer = 0u,
			CollisionMask = 2u // 只感应玩家（物理层 2）
		};
		_sensor.AddChild(new CollisionShape2D {
			Shape = new CircleShape2D { Radius = PickupRadius }
		});
		AddChild(_sensor);
		Log.Debug($"声波刷新点[{Name}] 就绪 位置={GlobalPosition.Round()} 半径={PickupRadius} 重生={RespawnTime}s");
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		// 未生成：倒计时后重新生成技能点
		if (!_active) {
			_respawnLeft -= dt;
			if (_respawnLeft <= 0f) {
				_active = true;
				Log.Debug($"声波刷新点[{Name}] 技能点重新生成");
			}
		}
		// 玩家重叠检测：拾取后须离开感应区（_needExit 置真），避免站在点上来回白嫖
		bool inside = false;
		Player touching = null;
		foreach (var body in _sensor.GetOverlappingBodies()) {
			if (body is Player player) {
				inside = true;
				touching = player;
				break;
			}
		}
		if (!inside) {
			_needExit = false;
		} else if (_active && !_needExit && touching != null) {
			// 拾取：立即刷新玩家声波冷却与空中次数限制
			touching.RefreshSoundWave();
			AudioManager.Instance?.PlaySe("pickup", 0.8f);
			_active = false;
			_respawnLeft = Mathf.Max(0.1f, RespawnTime);
			_needExit = true;
			Log.Debug($"声波刷新点[{Name}] 被玩家拾取，{RespawnTime}s 后重新生成");
		}
		QueueRedraw();
	}

	public override void _Draw() {
		// 底座（基座示意，像素风占位）
		DrawRect(new Rect2(-7, 2, 14, 4), new Color(0.3f, 0.36f, 0.46f));
		DrawRect(new Rect2(-7, 2, 14, 4), new Color(0.12f, 0.14f, 0.2f), false, 1f);
		DrawRect(new Rect2(-3, -2, 6, 4), new Color(0.2f, 0.25f, 0.33f));
		if (!_active) {
			// 未生成：底座上留一个暗色“空槽”点
			DrawRect(new Rect2(-1.5f, -9, 3, 3), new Color(0.18f, 0.2f, 0.25f));
			DrawRect(new Rect2(-1.5f, -9, 3, 3), new Color(0.1f, 0.11f, 0.15f), false, 1f);
			return;
		}
		// 技能点：上下浮动的菱形波光球 + 外圈提示环
		float bob = Mathf.Sin(_time * Mathf.Tau / 1.6f) * 2.5f;
		var c = new Vector2(0, -10 + bob);
		var orb = new Color(0.55f, 0.92f, 1f);
		Vector2[] diamond = {
			c + new Vector2(0, -5), c + new Vector2(5, 0),
			c + new Vector2(0, 5), c + new Vector2(-5, 0)
		};
		DrawColoredPolygon(diamond, new Color(orb, 0.95f));
		var edge = new Color(0.12f, 0.4f, 0.55f);
		DrawPolyline(new[] { diamond[0], diamond[1], diamond[2], diamond[3], diamond[0] }, edge, 1f, false);
		DrawRect(new Rect2(c.X - 1.5f, c.Y - 1.5f, 3, 3), new Color(1f, 1f, 1f, 0.9f));
		DrawArc(c, 9f + bob * 0.25f, 0f, Mathf.Tau, 20, new Color(orb, 0.3f), 1f, false);
		DrawArc(c, 4f, 0f, Mathf.Tau, 12, new Color(orb, 0.25f), 1f, false);
	}
}
