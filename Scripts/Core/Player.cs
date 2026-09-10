using Godot;

/// <summary>
/// 玩家控制器：左右移动（地面/空中加减速的惯性移动）、跳跃（土狼时间/跳跃缓冲/可变跳跃高度）、
/// 唯一的发声键 J（点按轻发声、长按蓄力——蓄力时减速）：蓄力期间方向键选择八个方向之一，
/// 松开 J 发出对应方向的扇形声波，不按方向键则发出全向声波。
/// **站在地面上时扇形不能朝下**（屏幕坐标 Y&gt;0 那半边，数学上的 180°~360°）：
/// 向下分量被压成水平，纯下方退回当前朝向；空中不受限制（空中朝下扇形配合后坐力是位移手段）。
/// 先按 J（此时未按任何方向键）再按方向键 → 蓄力期间方向键只瞄准、不移动（瞄准锁定）；
/// 先按方向键再按 J → 不影响正常移动（保持原蓄力减速规则，可边移动边瞄准）。
/// 空中按下 J 触发短暂“子弹时间”：（快速进入）时间缩放到极慢 BulletTimeScale（默认 0.025×），
/// 窗口/缓动按真实时间固定长短（快速进入、快速恢复）；慢放期间玩家保持进入时的运动趋势
/// （水平速度纯惯性、垂直仅受缩放后重力，抛物线在慢动作中延续），松开 J 释放声波后结束；地面按下不触发。
/// 每次空中（离开地面→落地/抓藤/重生）只允许触发 1 次子弹时间、释放 1 次声波；
/// 声波刷新点拾取（RefreshSoundWave）可立即刷新冷却并恢复次数。
/// 空中释放扇形声波时向声波反方向位移（后坐力冲量 AirFanRecoilSpeed）；
/// 只要本次蓄力由空中发起（子弹时间），即便慢放中落到地面才松开也照样反冲。
/// 扇形声波发射后有短暂方向键锁定期（FireMoveLockout）：发射瞬间仍按住的左右键不驱动移动，
/// 锁定期内松开该键立即解锁、重新按下立即生效（空中同规则），避免发射后未及时松键导致惯性移动。
/// 瞄准宽限（AimGraceWindow）：先松开方向键、随后极短时间内松开 J，仍按最后一次瞄准方向发射扇形声波。
/// 经声波放大器放大的强声波（强度 &gt; 1.0）经过玩家时会推动玩家：推力 = (强度-1) × WavePushVelocity。
/// 发声冷却期间玩家闪光提示（身上灯光 + 身体高亮）：开始时正常频率闪烁，快结束时快速闪烁几下。
/// 存档点（Checkpoint）接触后调用 SetRespawnPoint 把本关复活点改到该处（落水/落深渊重生时生效）。
/// 死亡重生播放蔚蓝式全屏过渡（DeathTransition + GDShader）：定格白闪→亮幕铺开→传送→亮幕收回。
/// 藤蔓抓取/攀爬/起跳。像素风格：物理位置取整，占位矩形绘制。
/// </summary>
public partial class Player : CharacterBody2D {
	[Export] public float MoveSpeed = 120f;
	/// <summary>地面水平加速度（px/s²）：数值越小转向/起步惯性越明显。</summary>
	[Export] public float GroundAcceleration = 950f;
	/// <summary>地面水平减速度（px/s²）：无输入时的滑行停止力度。</summary>
	[Export] public float GroundDeceleration = 1000f;
	/// <summary>空中水平加速度（px/s²）：空中转向比地面更迟缓。</summary>
	[Export] public float AirAcceleration = 450f;
	/// <summary>空中水平减速度（px/s²）：越小空中惯性保持越久。</summary>
	[Export] public float AirDeceleration = 150f;
	/// <summary>J 蓄力时的移速倍率（蓄力时减速）。</summary>
	[Export] public float OmniChargeMoveFactor = 0.35f;
	/// <summary>声波释放后的冷却时间（秒），冷却期间无法再次蓄力/释放。</summary>
	[Export] public float OmniCooldown = 0.5f;
	[Export] public float JumpVelocity = -320f;
	[Export] public float VineClimbSpeed = 90f;
	/// <summary>土狼时间：离开平台后仍可起跳的宽限（秒）。</summary>
	[Export] public float CoyoteTime = 0.08f;
	/// <summary>跳跃预输入：落地前提前按跳的缓冲（秒）。</summary>
	[Export] public float JumpBufferTime = 0.12f;
	/// <summary>子弹时间的时间缩放（0~1，越小越慢）。空中按下 J 触发。</summary>
	[Export] public float BulletTimeScale = 0.025f;
	/// <summary>子弹时间窗口时长（真实秒）：慢放状态的总持续时间；以真实时间计时，
	/// 不受时标缩放影响，保证“快速进入极慢状态、快速恢复”。</summary>
	[Export] public float BulletTimeDuration = 1.0f;
	/// <summary>子弹时间缓入时长（真实秒）：由正常速度快速平滑减到慢放。</summary>
	[Export] public float BulletTimeEaseIn = 0.08f;
	/// <summary>子弹时间缓出时长（真实秒）：由慢放快速平滑恢复到正常速度。</summary>
	[Export] public float BulletTimeEaseOut = 0.12f;
	/// <summary>扇形声波半角（度），总张角为其两倍。</summary>
	[Export] public float FanHalfAngleDeg = 30f;
	/// <summary>空中释放扇形声波的反向后坐力速度（px/s）：沿声波反方向施加的冲量（位移一段距离）。</summary>
	[Export] public float AirFanRecoilSpeed = 240f;
	/// <summary>扇形声波发射后的方向键锁定期（秒）：期间发射瞬间仍按住的左右方向键不驱动移动
	/// （防止发射后未及时松开方向键导致惯性移动）；锁定期内松开该键立即解锁，重新按下视为新输入立即生效。</summary>
	[Export] public float FireMoveLockout = 0.12f;
	/// <summary>扇形声波瞄准方向宽限（秒）：松开 J 前该间隔内先松开方向键，仍按最后一次瞄准方向发射扇形声波。</summary>
	[Export] public float AimGraceWindow = 0.12f;
	/// <summary>强声波推动：每单位（强度 - 1）的推力速度（px/s）。
	/// 放大后的声波强度可超过 1.0 上限，大于 1.0 的部分会推动玩家，推力随强度增大。</summary>
	[Export] public float WavePushVelocity = 260f;
	/// <summary>强声波推力的速度上限（px/s），防止多段放大叠加后推力失控。</summary>
	[Export] public float MaxWavePush = 700f;
	/// <summary>冷却提示：正常闪烁周期（秒）。</summary>
	[Export] public float FlashPeriod = 0.12f;
	/// <summary>冷却提示：快结束时的快速闪烁周期（秒）。</summary>
	[Export] public float FlashFastPeriod = 0.05f;
	/// <summary>剩余冷却低于该值（秒）时切换为快速闪烁。</summary>
	[Export] public float FlashFastThreshold = 0.16f;

	private const float OmniChargeMax = 0.7f;
	private const float MinIntensity = 0.35f;
	private const float Gravity = 980f;
	private const float MaxFallSpeed = 520f;

	public float Facing { get; private set; } = 1f;
	public bool IsCharging { get; private set; }
	public bool IsBulletTime { get; private set; }
	public bool OnVine { get; private set; }
	/// <summary>J 蓄力进度（0~1）。</summary>
	public float Charge => Mathf.Clamp(_charge / OmniChargeMax, 0f, 1f);
	/// <summary>蓄力期间选定的发射方向（八向，未按方向键为 0）。</summary>
	public Vector2 BulletAim { get; private set; } = Vector2.Zero;

	private WaveField _waveField;
	private Area2D _vineSensor;
	private Vine _vine;
	private PointLight2D _cooldownLight;
	private CanvasLayer _bulletTimeOverlay;
	/// <summary>死亡过渡（蔚蓝式全屏 GDShader 效果，_Ready 自建，无需改场景文件）。</summary>
	private DeathTransition _deathTransition;
	/// <summary>死亡过渡进行中：玩家冻结（不响应输入/物理），等待过渡在满屏瞬间传送。</summary>
	private bool _respawning;
	private float _vineS = 1f;
	private Vector2 _spawnPos;
	private float _coyote;
	private float _jumpBuffer;
	private float _charge;
	private float _grabCooldown;
	/// <summary>左右移动方向（-1/0/1），左右同按时最后按下优先。</summary>
	private float _moveDirX;
	/// <summary>声波释放后的剩余冷却（秒）。</summary>
	private float _emitCooldown;
	/// <summary>本次按下是否进入蓄力（非冷却时按下置真）。</summary>
	private bool _chargingActive;
	/// <summary>本次蓄力是否为“先按 J 后按方向键”的瞄准锁定：蓄力期间方向键只瞄准、不移动。</summary>
	private bool _aimLocked;
	/// <summary>本次空中（离开地面→落地/抓藤/重生）是否已触发过子弹时间（每空中最多 1 次）。</summary>
	private bool _airBulletTimeUsed;
	/// <summary>本次空中（离开地面→落地/抓藤/重生）是否已释放过声波（每空中最多 1 次）。</summary>
	private bool _airWaveUsed;
	/// <summary>本次蓄力是否由空中发起（J 在空中按下触发子弹时间）：即使慢放中落到地面才松开，
	/// 后坐力仍按“空中扇形声波”处理（否则第二次经刷新点刷新后的声波会因瞬时着地而丢失反冲）。</summary>
	private bool _airCharge;
	/// <summary>扇形声波发射后的方向键锁定期剩余（秒）。</summary>
	private float _fireLockTime;
	/// <summary>发射瞬间按住且未松开的左/右方向键（锁定期内不驱动移动）。</summary>
	private bool _fireLockLeft;
	private bool _fireLockRight;
	/// <summary>累计真实时间（秒，不受 Engine.TimeScale 缩放）：瞄准宽限等输入手感计时用。</summary>
	private float _time;
	/// <summary>最近一次有效的瞄准方向及其时刻：先松开方向键、宽限期内松开 J 仍按此方向发射扇形声波。</summary>
	private Vector2 _lastAim = Vector2.Zero;
	private float _lastAimAt = -1e9f;
	/// <summary>子弹时间窗口剩余（真实秒）。</summary>
	private float _bulletTimeLeft;
	/// <summary>子弹时间窗口已过（真实秒）。</summary>
	private float _bulletTimeElapsed;
	/// <summary>本次子弹时间缓入的起始时标（通常为 1.0）。</summary>
	private float _bulletTimeFromScale = 1f;
	/// <summary>子弹时间前/恢复目标的正常时标。</summary>
	private double _prevTimeScale = 1.0;
	/// <summary>是否正在执行时标过渡（如提前松键后的缓出恢复）。</summary>
	private bool _timeScaleBlending;
	private float _timeScaleBlendT;
	private float _timeScaleFrom;
	private float _timeScaleTo;
	private float _timeScaleDuration;
	private float _flashTimer;
	private bool _flashOn;

	public override void _Ready() {
		_spawnPos = GlobalPosition;
		_vineSensor = GetNode<Area2D>("VineSensor");
		_cooldownLight = GetNodeOrNull<PointLight2D>("CooldownLight");
		_bulletTimeOverlay = GetNodeOrNull<CanvasLayer>("BulletTimeOverlay");
		if (_bulletTimeOverlay != null) {
			_bulletTimeOverlay.Visible = false;
		}
		_waveField = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
		if (_waveField == null) {
			GD.PushWarning("场景中缺少组 wave_field 的 WaveField 节点，声波功能不可用");
		}
		// 死亡过渡：代码自建全屏着色器层（层号 20），不依赖场景文件
		_deathTransition = new DeathTransition();
		AddChild(_deathTransition);
	}

	public override void _ExitTree() {
		// 子弹时间内或时标缓动中离开场景（过关/重开等）时恢复时间缩放，避免影响后续场景
		if (IsBulletTime || _timeScaleBlending) {
			Engine.TimeScale = _prevTimeScale;
			_timeScaleBlending = false;
		}
	}

	public override void _PhysicsProcess(double delta) {
		if (_respawning) {
			// 死亡过渡中：玩家冻结在死亡处（不响应输入/重力/抓藤），
			// 由 DeathTransition 在亮幕铺满的瞬间调用 TeleportToRespawn 传送
			return;
		}
		float dt = (float)delta;
		// 真实时间 delta：子弹时间等时标缩放只影响游戏内世界（重力/声波/蓄力按缩放推进），
		// 输入手感类计时（发射锁定/瞄准宽限/窗口与缓动）以真实时间计，不被慢放拉长
		float realDt = ToRealDt(dt);
		_time += realDt;
		if (OnVine) {
			UpdateVine(dt);
			return;
		}
		_emitCooldown = Mathf.Max(0f, _emitCooldown - dt);
		UpdateCooldownFlash(dt);
		bool leftHeld = Input.IsActionPressed("move_left");
		bool rightHeld = Input.IsActionPressed("move_right");
		bool upHeld = Input.IsActionPressed("move_up");
		bool downHeld = Input.IsActionPressed("move_down");
		// 扇形声波发射后的方向键锁定期：锁定期内松开被锁定的键立即解锁（重新按下视为新输入，
		// 立即可移动，包括在空中时）；窗口结束全部解锁（真实时间计时，不受时标缩放影响）
		if (_fireLockTime > 0f) {
			_fireLockTime = Mathf.Max(0f, _fireLockTime - realDt);
			if (Input.IsActionJustReleased("move_left")) {
				_fireLockLeft = false;
			}
			if (Input.IsActionJustReleased("move_right")) {
				_fireLockRight = false;
			}
			if (_fireLockTime <= 0f) {
				_fireLockLeft = false;
				_fireLockRight = false;
			}
		}
		// 水平移动只由左右键决定：斜向不减慢；左右同按时以最后按下的键为准
		float rawMoveX = UpdateHorizontalMoveInput(leftHeld, rightHeld);
		// 锁定期内：当前生效方向若来自“发射瞬间按住且未松开”的键，视为无移动输入
		if (_fireLockTime > 0f) {
			bool blocked = rawMoveX < 0f ? (_fireLockLeft && leftHeld) : rawMoveX > 0f ? (_fireLockRight && rightHeld) : false;
			if (blocked) {
				rawMoveX = 0f;
			}
		}
		// 瞄准支持八向：横向取最后按下方向，纵向由上下键直接合成
		float aimY = (downHeld ? 1f : 0f) - (upHeld ? 1f : 0f);
		Vector2 aimInput = new(rawMoveX, aimY);
		if (aimInput.LengthSquared() > 0.01f) {
			aimInput = aimInput.Normalized();
		}
		// 瞄准锁定（先按 J 后按方向键）：方向键改为纯瞄准，不驱动移动
		float moveX = _aimLocked ? 0f : rawMoveX;
		bool emitHeld = Input.IsActionPressed("emit_omni");

		if (Input.IsActionJustPressed("emit_omni")) {
			_charge = 0f;
			_chargingActive = false;
			_aimLocked = false;
			_airCharge = false;
			// 新蓄力开始：清空瞄准宽限记录（避免沿用上一次蓄力的瞄准方向误发扇形声波）
			_lastAimAt = -1e9f;
			if (_emitCooldown > 0f) {
				// 冷却中：忽略本次输入
			} else if (!IsOnFloor() && (_airBulletTimeUsed || _airWaveUsed)) {
				// 本次空中的子弹时间/声波已用尽（每空中各 1 次）：忽略本次输入，
				// 落地/抓藤/重生或拾取“声波刷新点”（RefreshSoundWave）后恢复
			} else {
				_chargingActive = true;
				// 先按 J 后按方向键 → 瞄准锁定：本次蓄力期间方向键只瞄准不移动；
				// 先按方向键再按 J → 不影响正常移动（保持原蓄力减速规则，可边移动边瞄准）
				_aimLocked = !(leftHeld || rightHeld || upHeld || downHeld);
				if (!IsOnFloor()) {
					// 空中按下：进入子弹时间（地面按下不触发）
					EnterBulletTime();
				}
			}
		}
		IsCharging = emitHeld && _chargingActive;
		if (IsCharging) {
			// 蓄力进度：子弹时间（慢放瞄准阶段）内按真实时间累计，使玩家能在慢放窗口内蓄满；
			// 其余时间按游戏时间累计，声波强度 = 蓄力比例
			_charge += IsBulletTime ? realDt : dt;
			// 蓄力期间实时更新八向瞄准（地面上不允许朝下，见 ConstrainAim）
			Vector2 aim = ConstrainAim(aimInput);
			BulletAim = aim;
			if (aim.LengthSquared() > 0.01f) {
				// 记录最近一次有效瞄准：先松开方向键、AimGraceWindow 内松开 J 仍按此方向发射扇形声波
				_lastAim = aim;
				_lastAimAt = _time;
			}
		}

		// 每帧推进子弹时间窗口计时，并按缓入/保持/缓出曲线更新全局时间缩放
		UpdateBulletTime(dt);

		if (!IsCharging && Input.IsActionJustReleased("emit_omni") && _chargingActive) {
			// 松开：按瞄准方向释放（有方向 → 扇形声波；无方向 → 全向声波）并进入冷却
			EmitAimedWave();
			_emitCooldown = OmniCooldown;
			_charge = 0f;
			_chargingActive = false;
			_aimLocked = false;
			ExitBulletTime();
		}

		if (Mathf.Abs(moveX) > 0.01f) {
			Facing = Mathf.Sign(moveX);
		}

		if (IsOnFloor()) {
			_coyote = CoyoteTime;
		} else {
			_coyote -= dt;
		}
		_jumpBuffer = Input.IsActionJustPressed("jump") ? JumpBufferTime : _jumpBuffer - dt;
		if (_jumpBuffer > 0f && _coyote > 0f) {
			Velocity = new Vector2(Velocity.X, JumpVelocity);
			_jumpBuffer = 0f;
			_coyote = 0f;
		}
		// 跳跃高度随按键时长变化：上升中松键立即截断上升速度
		if (Input.IsActionJustReleased("jump") && Velocity.Y < 0f) {
			Velocity = new Vector2(Velocity.X, Velocity.Y * 0.45f);
		}

		if (!IsOnFloor()) {
			Velocity = new Vector2(Velocity.X, Mathf.Min(Velocity.Y + Gravity * dt, MaxFallSpeed));
		} else if (Velocity.Y > 0f) {
			Velocity = new Vector2(Velocity.X, 0f);
		}

		// 计算水平目标速度：蓄力减速；子弹时间保持进入时的运动趋势（纯惯性，不施加转向/减速）
		float targetX;
		if (IsBulletTime) {
			// 子弹时间：水平速度保持不变——维持进入慢放时的运动趋势（如右上方跳仍向右上方运动）；
			// 垂直方向仅受缩放后的重力，抛物线在慢动作中延续
			targetX = Velocity.X;
		} else {
			float factor = IsCharging ? OmniChargeMoveFactor : 1f;
			targetX = moveX * MoveSpeed * factor;
		}
		// 有目标速度时用加速度逼近（转向/起步有惯性）；无目标时用减速度滑行
		float accel;
		if (Mathf.Abs(targetX) > 0.01f) {
			accel = IsOnFloor() ? GroundAcceleration : AirAcceleration;
		} else {
			accel = IsOnFloor() ? GroundDeceleration : AirDeceleration;
		}
		Velocity = new Vector2(Mathf.MoveToward(Velocity.X, targetX, accel * dt), Velocity.Y);
		MoveAndSlide();

		// 落地：本次“空中”结束，恢复子弹时间/声波次数（每空中最多子弹时间 1 次、声波 1 次）
		if (IsOnFloor()) {
			_airBulletTimeUsed = false;
			_airWaveUsed = false;
		}

		// 强声波推动：经声波放大器放大的声波（强度 > 1.0）经过玩家时推动玩家，推力随强度增大；
		// 每道声波对玩家只推一次（WaveField 内按元素去重），沿该段声波的传播方向
		if (_waveField != null && _waveField.QueryPushAt(GlobalPosition, GetInstanceId(), out float pushIntensity, out Vector2 pushDir)) {
			float push = Mathf.Min((pushIntensity - 1f) * WavePushVelocity, MaxWavePush);
			Velocity += pushDir * push;
			Log.Debug($"强声波推动玩家 强度={pushIntensity:F2} 推力={push:F0} 方向={pushDir}");
		}

		_grabCooldown = Mathf.Max(0f, _grabCooldown - dt);
		if (!IsBulletTime && Input.IsActionPressed("move_up")) {
			TryGrabVine();
		}
		SnapAndRedraw();
	}

	private void EnterBulletTime() {
		// 本次空中只允许触发一次子弹时间
		_airBulletTimeUsed = true;
		// 本次蓄力由空中发起：后坐力按“空中扇形声波”处理（即便慢放中落地）
		_airCharge = true;
		IsBulletTime = true;
		BulletAim = Vector2.Zero;
		_bulletTimeElapsed = 0f;
		_bulletTimeLeft = BulletTimeDuration;
		// 记录进入时的时标作为缓入起点；若上一次缓出未结束，沿用之前记录的原始时标
		_bulletTimeFromScale = (float)Engine.TimeScale;
		if (!_timeScaleBlending) {
			_prevTimeScale = Engine.TimeScale;
		}
		_timeScaleBlending = false; // 窗口内由缓动曲线接管时标
		if (_bulletTimeOverlay != null) {
			_bulletTimeOverlay.Visible = true;
		}
		Log.Debug($"进入子弹时间 缩放={BulletTimeScale} 窗口={BulletTimeDuration}s(真实秒) 缓入={BulletTimeEaseIn}s 缓出={BulletTimeEaseOut}s");
		AudioManager.Instance?.PlaySe("bullet_time", 0.6f);
	}

	/// <summary>每物理帧推进子弹时间窗口/时标缓动（全部按真实时间计：窗口与缓动时长固定，
	/// 不受时标缩放影响——慢放多久结束、恢复多快由玩家实时体感决定）。窗口自然结束后仅恢复时标，蓄力与瞄准继续。</summary>
	private void UpdateBulletTime(float dt) {
		float realDt = ToRealDt(dt);
		if (IsBulletTime) {
			_bulletTimeElapsed += realDt;
			_bulletTimeLeft = Mathf.Max(0f, _bulletTimeLeft - realDt);
			Engine.TimeScale = ComputeBulletTimeScale();
			if (_bulletTimeLeft <= 0f) {
				EndBulletTimeWindow();
			}
		} else if (_timeScaleBlending) {
			// 非子弹时间期间：处理提前松键/抓藤蔓等触发的缓出恢复
			UpdateTimeScaleBlend(realDt);
		}
	}

	/// <summary>把受 Engine.TimeScale 缩放的物理帧 delta 换算为真实时间 delta（约 1/60 基准）。</summary>
	private static float ToRealDt(float dt) {
		return dt / Mathf.Max((float)Engine.TimeScale, 0.01f);
	}

	/// <summary>计算窗口内时标包络：缓入（快→慢）→ 保持慢放 → 缓出（慢→快）。</summary>
	private float ComputeBulletTimeScale() {
		float duration = Mathf.Max(BulletTimeDuration, 0.01f);
		float elapsed = Mathf.Clamp(_bulletTimeElapsed, 0f, duration);
		// 缓入/缓出各自最多占窗口一半，避免窗口太短时相互侵占
		float easeIn = Mathf.Min(Mathf.Max(0f, BulletTimeEaseIn), duration * 0.5f);
		float easeOut = Mathf.Min(Mathf.Max(0f, BulletTimeEaseOut), duration * 0.5f);
		float tIn = easeIn / duration;
		float tOut = 1f - easeOut / duration;
		float t = elapsed / duration;
		float envelope;
		if (t < tIn) {
			envelope = EaseInOut(t / tIn);
		} else if (t > tOut) {
			envelope = EaseInOut((1f - t) / (1f - tOut));
		} else {
			envelope = 1f;
		}
		return Mathf.Lerp(_bulletTimeFromScale, BulletTimeScale, envelope);
	}

	/// <summary>窗口自然结束：隐藏覆盖层并开始把时标缓动回正常值。</summary>
	private void EndBulletTimeWindow() {
		if (!IsBulletTime) {
			return;
		}
		IsBulletTime = false;
		if (_bulletTimeOverlay != null) {
			_bulletTimeOverlay.Visible = false;
		}
		BeginTimeScaleBlend((float)Engine.TimeScale, (float)_prevTimeScale, BulletTimeEaseOut);
		Log.Debug("子弹时间窗口结束，时标缓出恢复");
	}

	/// <summary>提前退出子弹时间（松开 J / 抓藤蔓等）：隐藏覆盖层并缓出恢复时标。</summary>
	private void ExitBulletTime() {
		if (!IsBulletTime) {
			return;
		}
		IsBulletTime = false;
		BulletAim = Vector2.Zero;
		if (_bulletTimeOverlay != null) {
			_bulletTimeOverlay.Visible = false;
		}
		BeginTimeScaleBlend((float)Engine.TimeScale, (float)_prevTimeScale, BulletTimeEaseOut);
		Log.Debug("退出子弹时间，时标缓出恢复");
	}

	/// <summary>立即结束子弹时间/时标过渡并恢复正常速度（重生、离开场景等重置用）。</summary>
	private void CancelBulletTime() {
		if (!IsBulletTime && !_timeScaleBlending) {
			return;
		}
		IsBulletTime = false;
		BulletAim = Vector2.Zero;
		_timeScaleBlending = false;
		Engine.TimeScale = _prevTimeScale;
		if (_bulletTimeOverlay != null) {
			_bulletTimeOverlay.Visible = false;
		}
	}

	/// <summary>开始一次时标过渡（例如缓出恢复到正常速度）。</summary>
	private void BeginTimeScaleBlend(float from, float to, float duration) {
		if (duration <= 0f || Mathf.Abs(from - to) < 0.0001f) {
			Engine.TimeScale = to;
			_timeScaleBlending = false;
			return;
		}
		_timeScaleBlending = true;
		_timeScaleBlendT = 0f;
		_timeScaleFrom = from;
		_timeScaleTo = to;
		_timeScaleDuration = duration;
	}

	/// <summary>推进时标过渡（SmoothStep 缓动）。</summary>
	private void UpdateTimeScaleBlend(float dt) {
		if (!_timeScaleBlending) {
			return;
		}
		_timeScaleBlendT += dt;
		float t = Mathf.Clamp(_timeScaleBlendT / _timeScaleDuration, 0f, 1f);
		Engine.TimeScale = Mathf.Lerp(_timeScaleFrom, _timeScaleTo, EaseInOut(t));
		if (t >= 1f) {
			Engine.TimeScale = _timeScaleTo;
			_timeScaleBlending = false;
		}
	}

	/// <summary>缓入缓出曲线（SmoothStep）：t=0 和 1 处导数为 0，速度变化更柔和。</summary>
	private static float EaseInOut(float t) {
		t = Mathf.Clamp(t, 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	/// <summary>
	/// 维护左右方向键的“最后按下优先”状态并返回当前横向输入（-1/0/1）。
	/// 斜向移动不再因向量归一化而减慢；左右同按时以最后按下的键为准。
	/// </summary>
	private float UpdateHorizontalMoveInput(bool leftHeld, bool rightHeld) {
		if (Input.IsActionJustPressed("move_left")) {
			_moveDirX = -1f;
		} else if (Input.IsActionJustPressed("move_right")) {
			_moveDirX = 1f;
		}
		// 松开当前生效的方向时，若另一方向仍按住，则回退到该方向
		if (Input.IsActionJustReleased("move_left") && _moveDirX < 0f && rightHeld) {
			_moveDirX = 1f;
		}
		if (Input.IsActionJustReleased("move_right") && _moveDirX > 0f && leftHeld) {
			_moveDirX = -1f;
		}
		if (!leftHeld && !rightHeld) {
			_moveDirX = 0f;
		} else if (_moveDirX == 0f) {
			// 兜底：若方向键在场景启动/掉帧时保持按下，但未捕获到按下事件，取当前唯一按住的键
			_moveDirX = rightHeld ? 1f : -1f;
		}
		return _moveDirX;
	}

	/// <summary>
	/// 地面扇形声波不能朝下：站在地面上时把瞄准方向的向下分量压掉（屏幕坐标 Y&gt;0 那半边，
	/// 即数学上的 180°~360°）——朝地面发声没有意义，也避免玩家把扇形"打进地里"白费一发。
	/// 斜下方（下+左/右）会贴地压成水平；纯下方则退回当前朝向的水平方向，
	/// 保证 J 永远能发出声波、且蓄力时的瞄准指示与实际发射方向一致。
	/// 空中不受限制（空中朝下扇形声波配合后坐力是位移手段）。
	/// </summary>
	private Vector2 ConstrainAim(Vector2 aim) {
		if (!IsOnFloor() || aim.Y <= 0f) {
			return aim;
		}
		Vector2 flat = new(aim.X, 0f);
		if (flat.LengthSquared() > 0.01f) {
			return flat.Normalized();
		}
		return new Vector2(Facing, 0f);
	}

	/// <summary>松开 J 时释放：有方向 → 扇形声波（八向）；无方向 → 全向声波。
	/// 在地面上时扇形方向不允许朝下（见 <see cref="ConstrainAim"/>）。</summary>
	private void EmitAimedWave() {
		float intensity = Mathf.Lerp(MinIntensity, 1f, Charge);
		bool airborne = !IsOnFloor();
		if (airborne) {
			// 本次空中只允许释放一次声波：释放即用掉额度（落地/抓藤/重生/拾取刷新点后恢复）
			_airWaveUsed = true;
		}
		// 瞄准宽限：松开 J 前方向键刚松开（AimGraceWindow 内）→ 仍按最后一次瞄准方向发射扇形声波，
		// 避免“先松方向键再快速松 J”被误判为全向；释放这一帧再夹一次，覆盖“空中蓄力、落地才松手”
		Vector2 aim = ConstrainAim(BulletAim);
		if (aim.LengthSquared() <= 0.01f && _lastAim.LengthSquared() > 0.01f && _time - _lastAimAt <= AimGraceWindow) {
			aim = ConstrainAim(_lastAim);
		}
		if (aim.LengthSquared() > 0.01f) {
			Vector2 dir = aim.Normalized();
			Log.Debug($"扇形声波 强度={intensity:F2} 方向={dir}");
			_waveField?.EmitFan(GlobalPosition, dir, FanHalfAngleDeg, intensity);
			AudioManager.Instance?.PlaySe("wave_fan", 0.35f + 0.5f * intensity);
			// 发射后的方向键锁定期：锁定此刻仍按住的左右键，防止发射后未及时松键导致惯性移动；
			// 锁定期内松开被锁的键立即解锁，重新按下立即生效
			_fireLockTime = Mathf.Max(0.05f, FireMoveLockout);
			_fireLockLeft = Input.IsActionPressed("move_left");
			_fireLockRight = Input.IsActionPressed("move_right");
			if (airborne || _airCharge) {
				// 空中释放扇形声波（或本次蓄力由空中发起、即便慢放中已落到地面）：向声波反方向位移
				Velocity -= dir * AirFanRecoilSpeed;
				Log.Debug($"空中扇形声波后坐力 冲量={-dir * AirFanRecoilSpeed}");
			}
		} else {
			Log.Debug($"全向声波 强度={intensity:F2}");
			_waveField?.EmitOmni(GlobalPosition, intensity);
			AudioManager.Instance?.PlaySe("wave_omni", 0.4f + 0.55f * intensity);
		}
		// 本次蓄力结束：清空中发起标记，下一次按下重新判定
		_airCharge = false;
	}

	/// <summary>
	/// 声波刷新点拾取回调：立即清空发声冷却，并重置本次空中的子弹时间/声波次数限制，
	/// 使玩家在空中也能立即再次触发子弹时间与释放声波。
	/// </summary>
	public void RefreshSoundWave() {
		_emitCooldown = 0f;
		_airBulletTimeUsed = false;
		_airWaveUsed = false;
		_flashTimer = 0f;
		_flashOn = false;
		Log.Debug("声波刷新：冷却清空，空中子弹时间/声波次数已重置");
	}

	/// <summary>
	/// 存档点激活回调：把本关复活点改到指定位置（通常为存档点节点位置 = 玩家脚底），
	/// 之后落水/落深渊等重生（Respawn）会回到该处。只在本次关卡内生效。
	/// </summary>
	public void SetRespawnPoint(Vector2 worldPos) {
		Vector2 pos = worldPos.Round();
		if (pos == _spawnPos) {
			return;
		}
		_spawnPos = pos;
		Log.Debug($"复活点更新为 {_spawnPos}");
	}

	/// <summary>当前复活点（世界坐标，玩家脚底）。</summary>
	public Vector2 RespawnPoint => _spawnPos;

	private void UpdateVine(float dt) {
		// 藤蔓上也要推进时标缓动（例如提前松键后的缓出恢复），避免时间缩放卡在半路
		UpdateBulletTime(dt);
		_emitCooldown = Mathf.Max(0f, _emitCooldown - dt);
		UpdateCooldownFlash(dt);
		bool leftHeld = Input.IsActionPressed("move_left");
		bool rightHeld = Input.IsActionPressed("move_right");
		bool upHeld = Input.IsActionPressed("move_up");
		bool downHeld = Input.IsActionPressed("move_down");
		float moveX = UpdateHorizontalMoveInput(leftHeld, rightHeld);
		float climbY = (downHeld ? 1f : 0f) - (upHeld ? 1f : 0f);
		if (Input.IsActionJustPressed("jump")) {
			// 藤蔓上起跳：继承藤蔓摆动速度
			Vector2 boost = _vine.GetVelocityAt(_vineS) * 1.25f;
			OnVine = false;
			_vine = null;
			_grabCooldown = 0.2f;
			Velocity = new Vector2(boost.X, JumpVelocity * 0.92f);
			_coyote = 0f;
			return;
		}
		_vineS = Mathf.Clamp(_vineS + climbY * VineClimbSpeed * dt / _vine.Length, 0.1f, 0.98f);
		GlobalPosition = _vine.GetPointAt(_vineS);
		Velocity = Vector2.Zero;
		if (Mathf.Abs(moveX) > 0.01f) {
			Facing = Mathf.Sign(moveX);
		}
		SnapAndRedraw();
	}

	private void TryGrabVine() {
		if (_grabCooldown > 0f) {
			return;
		}
		foreach (var area in _vineSensor.GetOverlappingAreas()) {
			if (area.GetParent() is Vine vine) {
				_vine = vine;
				_vineS = vine.WorldToS(GlobalPosition);
				Log.Debug($"抓住藤蔓 s={_vineS:F2}");
				OnVine = true;
				Velocity = Vector2.Zero;
				// 抓住藤蔓：取消蓄力与子弹时间状态
				_charge = 0f;
				_chargingActive = false;
				_aimLocked = false;
				_airCharge = false;
				// 抓藤视为结束本次“空中”：恢复子弹时间/声波次数
				_airBulletTimeUsed = false;
				_airWaveUsed = false;
				IsCharging = false;
				ExitBulletTime();
				return;
			}
		}
	}

	/// <summary>
	/// 死亡重生：播放蔚蓝式死亡过渡——世界定格 + 全屏白闪 → 亮幕从死亡点像素化铺开 →
	/// 满屏一瞬传回复活点（相机同步归位）→ 亮幕从复活点收回。
	/// 过渡期间玩家冻结、输入无效；无过渡节点（着色器缺失等）时退化为立即传送。
	/// </summary>
	public void Respawn() {
		if (_respawning) {
			return; // 过渡中重复触发（如连续进入危险区）忽略
		}
		Log.Warn($"玩家死亡，复活点 {_spawnPos}");
		AudioManager.Instance?.PlaySe("death");
		CancelBulletTime();
		_respawning = true;
		// 冻结：清空速度与蓄力/冷却提示，避免过渡期间继续下落或响应输入
		Velocity = Vector2.Zero;
		_charge = 0f;
		_chargingActive = false;
		_aimLocked = false;
		_airCharge = false;
		IsCharging = false;
		_flashOn = false;
		if (_cooldownLight != null) {
			_cooldownLight.Enabled = false;
		}
		if (_deathTransition == null) {
			TeleportToRespawn();
			_respawning = false;
			return;
		}
		_deathTransition.Play(GlobalPosition, _spawnPos, TeleportToRespawn, () => _respawning = false);
	}

	/// <summary>传送到复活点并重置状态：由死亡过渡在亮幕铺满（全屏不可见）的瞬间回调。</summary>
	private void TeleportToRespawn() {
		GlobalPosition = _spawnPos;
		Velocity = Vector2.Zero;
		OnVine = false;
		_vine = null;
		_emitCooldown = 0f;
		_airBulletTimeUsed = false;
		_airWaveUsed = false;
		_fireLockTime = 0f;
		_fireLockLeft = false;
		_fireLockRight = false;
		// 相机若开了平滑：立即归位，避免亮幕收回时相机还在慢慢飘
		GetNodeOrNull<Camera2D>("Camera2D")?.ResetSmoothing();
		SnapAndRedraw();
		AudioManager.Instance?.PlaySe("respawn", 0.7f);
		Log.Debug($"玩家已传送到复活点 {_spawnPos}");
	}

	/// <summary>冷却闪光：开始正常频率闪烁，快结束时快速闪烁几下；同步控制身上灯光。</summary>
	private void UpdateCooldownFlash(float dt) {
		if (_emitCooldown > 0f) {
			float period = _emitCooldown <= FlashFastThreshold ? FlashFastPeriod : FlashPeriod;
			_flashTimer -= dt;
			if (_flashTimer <= 0f) {
				_flashTimer += period;
				_flashOn = !_flashOn;
			}
		} else {
			_flashTimer = 0f;
			_flashOn = false;
		}
		if (_cooldownLight != null) {
			_cooldownLight.Enabled = _flashOn;
		}
	}

	private void SnapAndRedraw() {
		// 慢动作期间（子弹时间/时标缓动）每帧位移可能不足 1 像素：跳过位置取整，保留亚像素运动趋势；
		// 否则每帧 Round() 会把 <0.5px 的位移吞掉（小数不累积），玩家会“卡”在空中、恢复后直线下落
		if (Engine.TimeScale >= 0.999f) {
			GlobalPosition = GlobalPosition.Round();
		}
		QueueRedraw();
	}

	public override void _Draw() {
		Color body = _flashOn ? new Color(0.85f, 0.98f, 1f) : new Color(0.92f, 0.94f, 1f);
		DrawRect(new Rect2(-7, -22, 14, 22), body);
		DrawRect(new Rect2(-7, -22, 14, 22), new Color(0.15f, 0.16f, 0.22f), false, 1f);
		DrawRect(new Rect2(1, -17, 2, 2), new Color(0.1f, 0.1f, 0.15f));
		DrawRect(new Rect2(-3, -17, 2, 2), new Color(0.1f, 0.1f, 0.15f));
		if (_flashOn) {
			// 冷却闪光：身体外圈亮框（配合 CooldownLight 灯光）
			DrawRect(new Rect2(-9, -24, 18, 26), new Color(0.4f, 0.85f, 1f, 0.6f), false, 1f);
		}

		float barY = -28f;
		if (IsCharging || _charge > 0f) {
			DrawRect(new Rect2(-10, barY, 20, 4), new Color(0f, 0f, 0f, 0.6f));
			DrawRect(new Rect2(-9, barY + 1, 18 * Charge, 2), new Color(0.3f, 0.9f, 1f));
		}
		if (IsCharging) {
			DrawAim();
		}
	}

	/// <summary>蓄力瞄准指示：有方向 → 琥珀色扇形 + 箭头；无方向且子弹时间中 → 全向提示圆环。</summary>
	private void DrawAim() {
		var center = new Vector2(0, -11);
		var amber = new Color(1f, 0.8f, 0.35f);
		if (BulletAim.LengthSquared() < 0.01f) {
			if (!IsBulletTime) {
				return;
			}
			DrawArc(center, 15f, 0f, Mathf.Tau, 24, new Color(amber, 0.6f), 1.5f, false);
			DrawArc(center, 21f, 0f, Mathf.Tau, 24, new Color(amber, 0.3f), 1.5f, false);
			return;
		}
		Vector2 dir = BulletAim.Normalized();
		float baseAng = dir.Angle();
		float radius = 52f;
		var pts = new Vector2[18];
		pts[0] = center;
		for (int i = 0; i <= 16; i++) {
			float ang = baseAng + Mathf.DegToRad(Mathf.Lerp(-FanHalfAngleDeg, FanHalfAngleDeg, i / 16f));
			pts[i + 1] = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * radius;
		}
		DrawColoredPolygon(pts, new Color(1f, 0.8f, 0.35f, 0.18f));
		DrawLine(center, pts[1], new Color(amber, 0.8f), 1f);
		DrawLine(center, pts[17], new Color(amber, 0.8f), 1f);
		Vector2 tip = center + dir * (radius - 6f);
		DrawLine(center, tip, new Color(amber, 0.9f), 2f);
		Vector2 side = dir.Orthogonal() * 4f;
		DrawLine(tip, tip - dir * 8f + side, new Color(amber, 0.9f), 2f);
		DrawLine(tip, tip - dir * 8f - side, new Color(amber, 0.9f), 2f);
	}
}
