using Godot;

/// <summary>
/// 存档点：玩家接触后，本关的复活点改为此处——之后落水/落深渊等重生（Player.Respawn）时回到该点。
/// - 自包含裸节点：感应区（Area2D，mask=2 只感应玩家）与占位绘制在 _Ready 自动生成，
///   无需搭子节点；本体不占物理层、不挡声波、不碰撞玩家；
/// - 节点原点即复活位置（玩家脚底），视觉向上绘制：摆放时把原点放在站立面（平台顶）上；
/// - 一关内摆放多个存档点时，只有最后接触的一个是当前复活点（其余旗面熄灭，重新接触可夺回）；
/// - 复活点只在本次关卡内生效（关卡场景重载后回到玩家出生点），不写入存档文件。
/// </summary>
public partial class Checkpoint : Node2D {
	/// <summary>接触感应半径（px）：玩家身体进入该范围即激活。</summary>
	[Export] public float ActivateRadius = 14f;

	private Area2D _sensor;
	private float _time;
	/// <summary>本存档点是否为当前复活点（同一关内唯一）。</summary>
	private bool _current;
	/// <summary>是否被激活过（用于绘制“已使用但已不是当前”的暗色旗面）。</summary>
	private bool _visited;
	/// <summary>激活瞬间的扩散环（1→0 衰减）。</summary>
	private float _flash;

	public override void _Ready() {
		AddToGroup("checkpoint");
		_sensor = new Area2D {
			Name = "TouchSensor",
			CollisionLayer = 0u,
			CollisionMask = 2u // 只感应玩家（物理层 2）
		};
		_sensor.AddChild(new CollisionShape2D {
			Shape = new CircleShape2D { Radius = ActivateRadius }
		});
		_sensor.BodyEntered += OnBodyEntered;
		AddChild(_sensor);
		Log.Debug($"存档点[{Name}] 就绪 复活位置={GlobalPosition.Round()} 感应半径={ActivateRadius}");
	}

	private void OnBodyEntered(Node2D body) {
		if (body is Player player) {
			Activate(player);
		}
	}

	/// <summary>激活：把本关复活点改到本节点位置，并把同关其他存档点熄灭。</summary>
	private void Activate(Player player) {
		player.SetRespawnPoint(GlobalPosition);
		if (!_current) {
			_flash = 1f;
			Log.Info($"存档点[{Name}] 激活，本关复活点改为此处 {GlobalPosition.Round()}");
			AudioManager.Instance?.PlaySe("checkpoint");
		}
		_visited = true;
		SetCurrent(true);
		foreach (var node in GetTree().GetNodesInGroup("checkpoint")) {
			if (node is Checkpoint other && other != this) {
				other.SetCurrent(false);
			}
		}
	}

	/// <summary>设置本存档点是否为当前复活点（切换旗面亮灭）。</summary>
	public void SetCurrent(bool value) {
		if (_current == value) {
			return;
		}
		_current = value;
		QueueRedraw();
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		_flash = Mathf.Max(0f, _flash - dt / 0.55f);
		QueueRedraw();
	}

	public override void _Draw() {
		// 当前复活点：底部呼吸光环 + 旗顶亮点
		if (_current) {
			float pulse = 0.5f + 0.5f * Mathf.Sin(_time * 2.4f);
			DrawArc(Vector2.Zero, 7f + pulse * 2f, 0f, Mathf.Tau, 16, new Color(0.5f, 0.92f, 1f, 0.2f + 0.18f * pulse), 1f, false);
			DrawRect(new Rect2(-1, -28, 2, 2), new Color(0.85f, 0.99f, 1f));
		}
		// 激活瞬间：扩散环
		if (_flash > 0f) {
			float k = 1f - _flash;
			DrawArc(Vector2.Zero, 6f + 24f * k, 0f, Mathf.Tau, 24, new Color(0.6f, 0.95f, 1f, 0.65f * _flash), 1.5f, false);
		}
	}
}
