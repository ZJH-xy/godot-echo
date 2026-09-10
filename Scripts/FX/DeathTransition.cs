using System;
using Godot;

/// <summary>
/// 蔚蓝式死亡过渡（全屏 GDShader，见 Assets/Shaders/death_transition.gdshader）：
/// 死亡瞬间世界定格 + 全屏白闪 → 亮幕从死亡点向外像素化铺开 → 满屏一瞬把玩家传回复活点
/// （相机同步归位）→ 亮幕从复活点向外收回。全程约 0.5s，快速简洁、明亮清新。
/// - 由 Player 在 _Ready 自动创建（CanvasLayer 层号 20，高于子弹时间覆盖层与关卡 HUD），
///   无需改场景文件；
/// - 计时全部用真实时间（Time.GetTicksMsec），定格期间时标被压到 FreezeScale 也不会卡住过渡；
/// - 收回中心在传送后等一帧再取：此时相机（含平滑归位）已更新，亮幕正好从玩家身上收回。
/// </summary>
public partial class DeathTransition : CanvasLayer {
	/// <summary>死亡定格时长（真实秒）：世界几乎暂停 + 白闪。</summary>
	private const float FreezeTime = 0.09f;
	/// <summary>定格时的时间缩放：取极小值而不是 0（0 会让物理 delta 为 0，
	/// 部分 move_and_slide 实现会算出 NaN）；与子弹时间同量级，视觉上就是定格。</summary>
	private const float FreezeScale = 0.03f;
	/// <summary>亮幕从死亡点铺满屏幕的时长（真实秒）。</summary>
	private const float CoverTime = 0.26f;
	/// <summary>满屏停留时长（真实秒）：此刻玩家已传送到复活点。</summary>
	private const float HoldTime = 0.07f;
	/// <summary>亮幕从复活点收回的时长（真实秒）。</summary>
	private const float RevealTime = 0.24f;
	/// <summary>白闪衰减时长（真实秒）：只留一记短促的柔和脉冲，不做全屏死白。</summary>
	private const float FlashTime = 0.09f;

	private ColorRect _rect;
	private ShaderMaterial _material;
	private bool _playing;
	/// <summary>过渡已进行的真实时间（秒）。</summary>
	private float _t;
	/// <summary>上一帧的真实时刻（秒），用于在时标为 0 时仍能推进过渡。</summary>
	private double _lastTicks;
	/// <summary>是否已执行“满屏瞬间”的传送回调。</summary>
	private bool _covered;
	/// <summary>传送后等待一帧再取收回中心（等相机归位）。</summary>
	private bool _waitRevealCenter;
	/// <summary>复活点世界坐标（换算收回中心用）。</summary>
	private Vector2 _revealWorld;
	/// <summary>定格结束后要恢复的时标。</summary>
	private float _restoreTimeScale = 1f;
	private Action _onCovered;
	private Action _onFinished;

	public override void _Ready() {
		Layer = 20; // 高于子弹时间覆盖层(5)与关卡 HUD(10)
		var shader = GD.Load<Shader>("res://Assets/Shaders/death_transition.gdshader");
		if (shader == null) {
			Log.Warn("死亡过渡着色器缺失（res://Assets/Shaders/death_transition.gdshader），死亡将直接传送");
			return;
		}
		_material = new ShaderMaterial { Shader = shader };
		_rect = new ColorRect {
			Name = "Flash",
			Color = Colors.White,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Material = _material,
			Visible = false
		};
		AddChild(_rect);
		_rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		UpdateScreenSize();
		GetViewport().SizeChanged += UpdateScreenSize;
		Log.Debug("死亡过渡就绪（GDShader）");
	}

	public override void _ExitTree() {
		// 场景切换/退出时若定格未结束，恢复时间缩放，避免影响后续场景
		if (_playing && Engine.TimeScale < _restoreTimeScale * 0.5) {
			Engine.TimeScale = _restoreTimeScale;
		}
	}

	private void UpdateScreenSize() {
		_material?.SetShaderParameter("screen_size", GetViewport().GetVisibleRect().Size);
	}

	/// <summary>
	/// 播放一次死亡过渡。
	/// </summary>
	/// <param name="deathWorld">死亡位置（亮幕铺开中心）</param>
	/// <param name="respawnWorld">复活点位置（亮幕收回中心）</param>
	/// <param name="onCovered">满屏瞬间调用：此时传送玩家并让相机归位</param>
	/// <param name="onFinished">过渡结束时调用：解除玩家冻结</param>
	public void Play(Vector2 deathWorld, Vector2 respawnWorld, Action onCovered = null, Action onFinished = null) {
		if (_playing || _rect == null) {
			// 过渡不可用（着色器缺失）或已在播放：直接结算，保证玩家不会卡在死亡状态
			onCovered?.Invoke();
			onFinished?.Invoke();
			return;
		}
		_playing = true;
		_t = 0f;
		_lastTicks = Time.GetTicksMsec() / 1000.0;
		_covered = false;
		_waitRevealCenter = false;
		_revealWorld = respawnWorld;
		_onCovered = onCovered;
		_onFinished = onFinished;
		_restoreTimeScale = (float)Engine.TimeScale;
		if (_restoreTimeScale <= 0.01f) {
			_restoreTimeScale = 1f;
		}
		_material.SetShaderParameter("cover_center", WorldToUv(deathWorld));
		_material.SetShaderParameter("reveal_center", new Vector2(0.5f, 0.5f));
		_material.SetShaderParameter("cover", 0f);
		_material.SetShaderParameter("reveal", 0f);
		_material.SetShaderParameter("flash", 1f);
		_rect.Visible = true;
		Log.Debug($"死亡过渡开始 死亡点={deathWorld.Round()} 复活点={respawnWorld.Round()}");
	}

	public override void _Process(double delta) {
		if (!_playing) {
			return;
		}
		// 传送后等一帧：相机已在上一帧渲染时归位，此刻取收回中心才是复活后的屏幕位置
		if (_waitRevealCenter) {
			_waitRevealCenter = false;
			_material.SetShaderParameter("reveal_center", WorldToUv(_revealWorld));
		}
		// 真实时间推进：定格期间时标被压到 FreezeScale（delta≈0），必须用挂钟时间
		double now = Time.GetTicksMsec() / 1000.0;
		float dt = Mathf.Clamp((float)(now - _lastTicks), 0f, 0.1f);
		_lastTicks = now;
		_t += dt;

		float flash = Mathf.Clamp(1f - _t / FlashTime, 0f, 1f);
		float cover = 0f;
		float reveal = 0f;
		if (_t < FreezeTime) {
			// 死亡定格：世界几乎停住（FreezeScale 而非 0），全屏白闪
			Engine.TimeScale = FreezeScale;
		} else {
			Engine.TimeScale = _restoreTimeScale;
			float ct = _t - FreezeTime;
			cover = EaseOut(Mathf.Clamp(ct / CoverTime, 0f, 1f));
			float rt = ct - CoverTime - HoldTime;
			if (rt > 0f) {
				reveal = EaseInOut(Mathf.Clamp(rt / RevealTime, 0f, 1f));
			}
		}
		// 满屏瞬间：传送玩家、相机归位（此后亮幕开始从复活点收回）
		if (!_covered && _t >= FreezeTime + CoverTime) {
			_covered = true;
			_waitRevealCenter = true;
			_onCovered?.Invoke();
		}
		if (_t >= FreezeTime + CoverTime + HoldTime + RevealTime) {
			Finish();
			return;
		}
		_material.SetShaderParameter("cover", cover);
		_material.SetShaderParameter("reveal", reveal);
		_material.SetShaderParameter("flash", flash);
	}

	private void Finish() {
		_playing = false;
		Engine.TimeScale = _restoreTimeScale;
		_rect.Visible = false;
		_material.SetShaderParameter("cover", 0f);
		_material.SetShaderParameter("reveal", 0f);
		_material.SetShaderParameter("flash", 0f);
		var finished = _onFinished;
		_onCovered = null;
		_onFinished = null;
		finished?.Invoke();
		Log.Debug("死亡过渡结束");
	}

	/// <summary>世界坐标 → 屏幕 UV（0~1）：按视口画布变换（含相机）换算，并夹进屏幕内。</summary>
	private Vector2 WorldToUv(Vector2 world) {
		Vector2 size = GetViewport().GetVisibleRect().Size;
		Vector2 screen = GetViewport().GetCanvasTransform() * world;
		return new Vector2(
			Mathf.Clamp(screen.X / Mathf.Max(1f, size.X), 0.02f, 0.98f),
			Mathf.Clamp(screen.Y / Mathf.Max(1f, size.Y), 0.02f, 0.98f));
	}

	/// <summary>缓出：铺开时先快后慢，收尾干净。</summary>
	private static float EaseOut(float t) {
		return 1f - (1f - t) * (1f - t);
	}

	/// <summary>缓入缓出（SmoothStep）：收回时两端柔和。</summary>
	private static float EaseInOut(float t) {
		return t * t * (3f - 2f * t);
	}
}
