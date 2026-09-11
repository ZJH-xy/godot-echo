using Godot;
using System;

/// <summary>
/// 关卡 HUD：关卡标题淡入淡出、教学提示对话框、过关结算覆盖层。
/// 提示对话框（关卡HUD.tscn 的 HintBox）由教学提示区 <see cref="HintZone"/> 触发：
/// ShowHint(owner, 文本, 按键) 从上方滑入弹出，ClearHint(owner) 淡出收起；
/// 对话框位置/样式全部在场景里调，脚本只负责显隐与动画。
/// </summary>
public partial class LevelHud : CanvasLayer {
	public static LevelHud Instance { get; private set; }

	[Export] public string TitleText = "序章 · 初啼";
	[Export] public string NextScene = "res://scenes/main.tscn";

	[Export] private ColorRect _titleColor;
	[Export] private Label _titleLabel;
	[Export] private Control _hintBox;
	[Export] private Label _hintText;
	[Export] private Control _hintKeyCap;
	[Export] private Label _hintKeyLabel;
	[Export] private Control _completeLayer;
	[Export] private Label _completeTitle;
	[Export] private Label _completeSub;

	[Export] private String _completeTitleText = "关卡完成！";
	[Export] private String _completeSubText = "即将前往下一章…";

	/// <summary>当前提示的发起者（提示区节点）：只有发起者自己的"离开"才能收起对话框，多个区域互不误清。</summary>
	private Node _hintOwner;
	/// <summary>开场标题还没开始淡出时收到的提示：先暂存，等标题淡出再弹（避免和标题叠在一起）。</summary>
	private Node _pendingOwner;
	private string _pendingText;
	private string _pendingKey;
	/// <summary>对话框静止时的上下偏移（场景里摆好的值，作为滑入/滑出的动画基准，首次弹出时记录）。</summary>
	private float _hintRestTop = float.NaN;
	private float _hintRestBottom = float.NaN;
	private Tween _hintTween;
	/// <summary>对话框是否处于显示状态（含弹出动画中）。</summary>
	private bool _hintShown;
	/// <summary>开场标题是否已开始淡出（此前收到的提示会暂存）。</summary>
	private bool _titleIntroFading;
	/// <summary>对话框滑入/滑出的距离（px）。</summary>
	private const float HintSlide = 8f;
	/// <summary>提示文字左边距：无按键帽 / 有按键帽（让开 KeyCap）。</summary>
	private const float HintTextLeftPlain = 14f;
	private const float HintTextLeftWithKey = 48f;

	public override void _Ready() {
		Instance = this;

		// 关卡 BGM：按关卡场景文件名选曲（prologue/echo/blind/mute/chorus），切关时同名曲不重头播放
		AudioManager.Instance?.PlayBgm(ResolveBgmName(), fadeIn: 1.2f);

		_completeLayer.Visible = false;
		_completeLayer.Modulate = new Color(1, 1, 1, 0);

		if (_hintBox != null) {
			_hintBox.Visible = false;
			_hintBox.Modulate = new Color(1, 1, 1, 0);
		}

		_titleLabel.Text = Tr(TitleText);
		_completeTitle.Text = Tr(_completeTitleText);
		_completeSub.Text = Tr(_completeSubText);
		_titleLabel.Modulate = new Color(1, 1, 1, 0);
		Log.Info($"进入关卡：{TitleText}");
		var tween = CreateTween();
		tween.TweenProperty(_titleLabel, "modulate", Colors.White, 0.6);
		tween.TweenInterval(1.8);
		// 标题开始淡出：这时才允许提示对话框弹出（开场头 2.4s 只演标题）
		tween.TweenCallback(Callable.From(OnTitleIntroFading));
		tween.TweenProperty(_titleColor, "modulate", new Color(1, 1, 1, 0), 0.8);
		tween.TweenProperty(_titleLabel, "modulate", new Color(1, 1, 1, 0), 0.8);
	}

	public override void _Input(InputEvent @event) {
		if (@event.IsActionPressed("pause")) {
			var scence = ResourceLoader.Load("res://Scenes/setting.tscn") as PackedScene;
			AddChild(scence.Instantiate<Control>());
		}
	}

	/// <summary>
	/// 本关 BGM 名 = 关卡场景文件名（prologue / echo / blind / mute / chorus）——
	/// 新增关卡只要把同名音频放进 Assets/Audio/bgm/ 即可，无需改脚本或场景；
	/// 没有对应音频（如临时测试场景）时退回通用 "level"。
	/// </summary>
	private string ResolveBgmName() {
		string name = GetTree().CurrentScene?.SceneFilePath?.GetFile().GetBaseName();
		if (string.IsNullOrEmpty(name) || ResourceResolver.ResolveAudio("bgm", name) == null) {
			return "level";
		}
		return name;
	}

	public override void _ExitTree() {
		if (Instance == this) {
			Instance = null;
		}
	}

	/// <summary>
	/// 弹出教学提示对话框：文本 + 可选按键帽（如 "J"）。
	/// owner 为提示的发起者（提示区节点），收起时只认它自己的 ClearHint；
	/// 开场标题还在演时先暂存，等标题淡出再弹。
	/// </summary>
	public void ShowHint(Node owner, string text, string key = "") {
		if (string.IsNullOrEmpty(text)) {
			return;
		}
		if (_hintBox == null || _hintText == null) {
			Log.Warn("关卡 HUD 缺少提示对话框节点（关卡HUD.tscn 的 HintBox），教学提示不会显示");
			return;
		}
		if (!_titleIntroFading) {
			_pendingOwner = owner;
			_pendingText = text;
			_pendingKey = key;
			return;
		}
		PopHint(owner, text, key);
	}

	/// <summary>收起提示对话框（只有发起者能收起自己的提示；尚未弹出的提示直接取消）。</summary>
	public void ClearHint(Node owner) {
		if (_pendingOwner == owner) {
			_pendingOwner = null;
			_pendingText = null;
			_pendingKey = null;
		}
		if (_hintOwner != owner) {
			return;
		}
		_hintOwner = null;
		_hintShown = false;
		if (_hintBox == null || float.IsNaN(_hintRestTop)) {
			return;
		}
		_hintTween?.Kill();
		_hintTween = CreateTween().SetParallel(true);
		_hintTween.TweenProperty(_hintBox, "modulate:a", 0f, 0.14f);
		_hintTween.TweenProperty(_hintBox, "offset_top", _hintRestTop - HintSlide * 0.6f, 0.18f)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
		_hintTween.TweenProperty(_hintBox, "offset_bottom", _hintRestBottom - HintSlide * 0.6f, 0.18f)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
		_hintTween.Chain().TweenCallback(Callable.From(HideHintIfIdle));
	}

	/// <summary>开场标题开始淡出：允许提示弹出，并补上暂存的那条。</summary>
	private void OnTitleIntroFading() {
		_titleIntroFading = true;
		if (string.IsNullOrEmpty(_pendingText)) {
			return;
		}
		Node owner = _pendingOwner;
		string text = _pendingText;
		string key = _pendingKey;
		_pendingOwner = null;
		_pendingText = null;
		_pendingKey = null;
		PopHint(owner, text, key);
	}

	/// <summary>立即弹出（或就地换成新文本）：从上方滑入 + 淡入。</summary>
	private void PopHint(Node owner, string text, string key) {
		_hintOwner = owner;
		_hintText.Text = text;
		bool hasKey = !string.IsNullOrEmpty(key) && _hintKeyCap != null && _hintKeyLabel != null;
		if (_hintKeyCap != null) {
			_hintKeyCap.Visible = hasKey;
		}
		if (hasKey) {
			_hintKeyLabel.Text = key;
		}
		_hintText.OffsetLeft = hasKey ? HintTextLeftWithKey : HintTextLeftPlain;
		if (float.IsNaN(_hintRestTop)) {
			// 首次弹出：记住场景里摆好的静止位置，作为后续动画的基准（动画只改 offset，不动锚点）
			_hintRestTop = _hintBox.OffsetTop;
			_hintRestBottom = _hintBox.OffsetBottom;
		}
		_hintTween?.Kill();
		_hintBox.Visible = true;
		if (_hintShown) {
			// 已在显示：只换文本，位置与透明度保持（不重播弹出动画）
			_hintBox.Modulate = Colors.White;
			_hintBox.OffsetTop = _hintRestTop;
			_hintBox.OffsetBottom = _hintRestBottom;
			return;
		}
		_hintShown = true;
		_hintBox.Modulate = new Color(1, 1, 1, 0);
		_hintBox.OffsetTop = _hintRestTop - HintSlide;
		_hintBox.OffsetBottom = _hintRestBottom - HintSlide;
		_hintTween = CreateTween().SetParallel(true);
		_hintTween.TweenProperty(_hintBox, "modulate:a", 1f, 0.16f);
		_hintTween.TweenProperty(_hintBox, "offset_top", _hintRestTop, 0.22f)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		_hintTween.TweenProperty(_hintBox, "offset_bottom", _hintRestBottom, 0.22f)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		Log.Debug($"提示弹出：{text}（按键={key}）");
	}

	/// <summary>淡出结束后再隐藏（淡出途中又被叫起来弹出时不隐藏）。</summary>
	private void HideHintIfIdle() {
		if (!_hintShown && _hintBox != null) {
			_hintBox.Visible = false;
		}
	}

	public void Complete() {
		if (_completeLayer.Visible) {
			return;
		}
		Log.Info($"关卡完成：{TitleText}，下个场景：{NextScene}");
		_completeLayer.Visible = true;
		var tween = CreateTween();
		tween.TweenProperty(_completeLayer, "modulate", Colors.White, 0.5);
		GetTree().CreateTimer(2.6).Timeout += () => GetTree().ChangeSceneToFile(NextScene);
	}
}
