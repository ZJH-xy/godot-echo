using Godot;

/// <summary>
/// 声控门：接到检测器 Triggered 信号后向上滑开（关闭碰撞，声波与玩家均可通过），
/// 可选自动关闭。关闭状态下属于地形层，会阻挡声波传播。
/// </summary>
public partial class EchoDoor : Node2D {
	[Export] public float SlideDistance = 140f;
	[Export] public float OpenDuration = 0.45f;
	[Export] public float AutoCloseDelay = 0f;
	[Export] public NodePath DetectorPath;
	[Export] public NodePath BodyPath = new NodePath("DoorBody");

	private StaticBody2D _body;
	private Vector2 _closedPos;
	private bool _open;
	private Tween _tween;

	public override void _Ready() {
		_body = GetNode<StaticBody2D>(BodyPath);
		_closedPos = _body.Position;
		if (DetectorPath == null || DetectorPath.IsEmpty) {
			Log.Warn($"门[{Name}] 未配置 DetectorPath，无法接收检测器信号");
			return;
		}
		var detector = GetNodeOrNull<SoundDetector>(DetectorPath);
		if (detector == null) {
			Log.Error($"门[{Name}] 找不到检测器：{DetectorPath}（请检查节点路径）");
			return;
		}
		detector.Triggered += OnDetectorTriggered;
		Log.Debug($"门[{Name}] 已连接检测器 {detector.Name}");
	}

	public void OnDetectorTriggered(float intensity) {
		Open();
	}

	public void Open() {
		if (_open) {
			return;
		}
		_open = true;
		AudioManager.Instance?.PlaySe("door", 0.6f);
		_tween?.Kill();
		_body.SetDeferred("collision_layer", 0);
		_body.SetDeferred("collision_mask", 0);
		_tween = CreateTween();
		_tween.TweenProperty(_body, "position", _closedPos + new Vector2(0, -SlideDistance), OpenDuration)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
		if (AutoCloseDelay > 0f) {
			GetTree().CreateTimer(AutoCloseDelay).Timeout += Close;
		}
		MarkSolidDirty();
	}

	public void Close() {
		if (!_open) {
			return;
		}
		_open = false;
		AudioManager.Instance?.PlaySe("door", 0.35f, 0.72f);
		_tween?.Kill();
		_tween = CreateTween();
		_tween.TweenProperty(_body, "position", _closedPos, OpenDuration)
			.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.InOut);
		_tween.Finished += () => {
			if (!_open) {
				_body.SetDeferred("collision_layer", 1);
				_body.SetDeferred("collision_mask", 0);
			}
		};
		MarkSolidDirty();
	}

	private void MarkSolidDirty() {
		var field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
		field?.MarkSolidDirty();
	}
}
