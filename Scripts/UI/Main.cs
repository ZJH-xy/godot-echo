using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 主菜单：
/// - 背景绘制呼吸的“回声圆环”（后续可换成背景图：见 <see cref="BuildLayers"/> 的 Background 节点）；
/// - **鼠标点击释放全向声波**（见 <see cref="SpawnShockwave"/>）：点击处向外扩一道冲击波，
///   把背景按径向推开（GDShader + BackBufferCopy 折射扭曲，像素风），并叠一圈青白亮环，
///   随后指数衰减淡出——与游戏内的声场是同一套语言；
/// - 语言按钮在中文 / English 间切换（切换后所有文本即时刷新，选择持久化）。
/// 视觉层全部在代码里搭建，**不改 scenes/main.tscn**：Background(-100) → BackBufferCopy(-90) →
/// Shockwave(-80) → 场景里的 UI(0)，既保证冲击波能采到背景，又保证菜单控件不被扭曲。
/// </summary>
public partial class Main : Control {
	/// <summary>同时存在的冲击波道数上限（与 menu_shockwave.gdshader 的 MaxWaves 一致）。</summary>
	private const int MaxWaves = 8;
	/// <summary>一道冲击波从点击点扩散到覆盖屏幕**对角**所需时长（秒）。
	/// 波前按视口对角线换算，所以屏幕多大都是这个手感（不因分辨率变快变慢）。</summary>
	private const float ShockwaveDuration = 0.85f;
	/// <summary>冲击波起爆延迟（秒）：点击后先闪一下原点，再向外推。</summary>
	private const float ShockwaveDelay = 0.02f;
	/// <summary>像素化块边长（px）：波前与扭曲按块跳变，保留像素风。</summary>
	private const float PixelCell = 6f;

	/// <summary>当前存活的一道冲击波（屏幕 UV 中心 + 起爆模拟时刻）。</summary>
	private struct Shockwave {
		public Vector2 Center;   // 屏幕 UV（0~1）
		public double Start;     // 起爆时刻（秒，真实时间）
	}

	private float _t;
	private readonly List<Shockwave> _waves = new();
	private ColorRect _shockwaveLayer;
	private ShaderMaterial _shockwaveMaterial;
	/// <summary>传给着色器的波参数（不足 MaxWaves 的槽位强度为 0）。</summary>
	private readonly Vector4[] _rippleParams = new Vector4[MaxWaves];
	/// <summary>屏幕纹理尺寸（px），随窗口变化更新。</summary>
	private Vector2 _screenSize = new(640f, 360f);
	private double _lastTicks;

	[Export] private Label _title;
	[Export] private Label _subtitle;
	[Export] private Button _continueButton;
	[Export] private Button _startButton;
	[Export] private Button _settingButton;
	[Export] private Button _quitButton;
	[Export] private Button _langButton;

	public const string SETTINGS_PATH = "user://settings.cfg";
	public string[] Level { get; } = [
		"res://Scenes/Level/prologue.tscn",
		"res://Scenes/Level/echo.tscn",
		"res://Scenes/Level/blind.tscn",
		"res://Scenes/Level/mute.tscn",
		"res://Scenes/Level/chorus.tscn"
	];

	public enum Language {
		Chinese,
		English
	}

	public override void _Ready() {
		Log.Init();
		Settings.Load();
		Settings.ClampToScreen(DisplayServer.ScreenGetSize());
		Settings.ApplyAll(GetWindow()); // 启动时应用已保存的分辨率与音量

		string savedLang = LoadLanguagePreference();
		if (string.IsNullOrEmpty(savedLang))
			savedLang = "zh";
		TranslationServer.SetLocale(savedLang);

		AudioManager.Instance?.PlayBgm("menu", fadeIn: 1.2f);

		BuildLayers();

		_continueButton.Pressed += OnContinue;
		_startButton.Pressed += OnStart;
		_settingButton.Pressed += OnSetting;
		_quitButton.Pressed += OnQuit;
		_langButton.Pressed += OnToggleLanguage;
	}

	// ==================== 背景与冲击波层 ====================

	/// <summary>
	/// 用代码搭建菜单的视觉层（不动 .tscn）：
	/// Background（纯色底，负 z 垫在 UI 之下；**后续换背景图**：把 main.tscn 里的背景 Sprite2D 摆在
	/// 这一层即可——只要 z_index 低于 Shockwave 就会被冲击波扭曲，若用默认 z=0 则把本节点的
	/// ZIndex 改成正值或给背景设负 z）→ BackBufferCopy（把背景拷进屏幕纹理）
	/// → Shockwave（全屏冲击波层，采样屏幕纹理做折射扭曲）。
	/// </summary>
	private void BuildLayers() {
		if (HasNode("Background")) {
			return; // 场景里已有（或已搭过）：不重复添加
		}
		// 垫底纯色背景（场景里的 BG 节点是 visible=false，这里不碰它，避免“改场景”）
		var background = new ColorRect {
			Name = "Background",
			Color = new Color(0.04f, 0.045f, 0.08f, 1f),
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = -100
		};
		AddChild(background);
		background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		// 冲击波要扭曲的是“背景”，所以先让引擎把当前画面拷进 back buffer 供着色器读取
		var copy = new BackBufferCopy {
			Name = "BackgroundCopy",
			CopyMode = BackBufferCopy.CopyModeEnum.Viewport,
			ZIndex = -90
		};
		AddChild(copy);

		var shader = GD.Load<Shader>("res://Assets/Shaders/menu_shockwave.gdshader");
		if (shader == null) {
			Log.Warn("主菜单冲击波着色器缺失（res://Assets/Shaders/menu_shockwave.gdshader），点击不会有扭曲效果");
			return;
		}
		_shockwaveMaterial = new ShaderMaterial { Shader = shader };
		// 着色器若写错，Godot 只会把报错丢进控制台、材质照旧不动——这里补一条日志便于排查
		if (shader.GetShaderUniformList().Count < 6) {
			Log.Warn("主菜单冲击波着色器疑似未编译通过（uniform 列表为空），请检查 res://Assets/Shaders/menu_shockwave.gdshader");
		}
		_shockwaveLayer = new ColorRect {
			Name = "Shockwave",
			Color = Colors.White,
			// 点击交给 Main._Input 统一处理（这样才能区分点空处与点按钮），本层不吃输入
			MouseFilter = MouseFilterEnum.Ignore,
			Material = _shockwaveMaterial,
			ZIndex = -80
		};
		AddChild(_shockwaveLayer);
		_shockwaveLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		UpdateScreenSize();
		GetViewport().SizeChanged += UpdateScreenSize;
		Log.Debug("主菜单冲击波层就绪（背景 + BackBufferCopy + menu_shockwave.gdshader）");
	}

	private void UpdateScreenSize() {
		_screenSize = GetViewport().GetVisibleRect().Size;
		_shockwaveMaterial?.SetShaderParameter("screen_size", _screenSize);
		_shockwaveMaterial?.SetShaderParameter("pixel_cell", PixelCell);
	}

	// ==================== 点击 → 全向声波 ====================

	/// <summary>
	/// 鼠标左键点击**空处**时释放一道全向冲击波；点在按钮/面板上则交给 UI，不放波。
	/// 这里不做“谁先消费事件”的假设：直接问视口“屏幕这个位置最上层是不是能吃鼠标的控件”，
	/// 从而稳定区分“点空处”与“点按钮”（按钮本身照常收到点击事件）。
	/// </summary>
	public override void _Input(InputEvent @event) {
		if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) {
			return;
		}
		if (IsOverUi(click.Position)) {
			return;
		}
		SpawnShockwave(click.Position);
	}

	/// <summary>屏幕位置（px）是否落在会消费鼠标的控件上（按钮、LanguageButton 之类）。</summary>
	private bool IsOverUi(Vector2 screenPos) {
		Control hovered = GetViewport().GuiGetHoveredControl();
		return hovered != null && hovered != this && hovered.MouseFilter != MouseFilterEnum.Ignore;
	}

	/// <summary>在屏幕位置 <paramref name="screenPos"/>（px）释放一道全向冲击波。</summary>
	public void SpawnShockwave(Vector2 screenPos) {
		Vector2 uv = new(
			Mathf.Clamp(screenPos.X / Mathf.Max(1f, _screenSize.X), 0f, 1f),
			Mathf.Clamp(screenPos.Y / Mathf.Max(1f, _screenSize.Y), 0f, 1f));
		if (_waves.Count >= MaxWaves) {
			_waves.RemoveAt(0); // 超出上限：让最早的先退场
		}
		_waves.Add(new Shockwave { Center = uv, Start = Now });
		// 与游戏内发声同一记音效（无 Assets/Audio 时 AudioManager 自己降级，不报错）
		AudioManager.Instance?.PlaySe("wave_omni", 0.35f);
		Log.Debug($"主菜单发声 origin={screenPos.Round()}");
	}

	/// <summary>挂钟时间（秒）：菜单时标恒为 1，但用真实时间更稳（切场景/拖窗口不跳变）。</summary>
	private static double Now => Time.GetTicksMsec() / 1000.0;

	private void UpdateShockwaves() {
		if (_shockwaveLayer == null) {
			return;
		}
		double now = Now;
		// 半径按视口对角线换算：屏幕多大都在 ShockwaveDuration 内铺满
		float maxRadius = _screenSize.Length();
		int used = 0;
		for (int i = _waves.Count - 1; i >= 0; i--) {
			Shockwave w = _waves[i];
			float life = (float)(now - w.Start);
			if (life >= ShockwaveDuration) {
				_waves.RemoveAt(i);
				continue;
			}
			float k = Mathf.Clamp((life - ShockwaveDelay) / Mathf.Max(0.01f, ShockwaveDuration - ShockwaveDelay), 0f, 1f);
			// 扩张用缓出曲线（起步猛、收尾慢），波前扩散更有“冲击”感
			float radius = maxRadius * (1f - (1f - k) * (1f - k));
			// 强度包络：极短的上冲 + 指数衰减，余波自己淡干净（不整屏发白）
			float env = (1f - Mathf.Exp(-life * 22f)) * Mathf.Exp(-life * 3.4f);
			_rippleParams[used++] = new Vector4(w.Center.X, w.Center.Y, radius, env);
		}
		for (int i = used; i < MaxWaves; i++) {
			_rippleParams[i] = Vector4.Zero;
		}
		_shockwaveMaterial.SetShaderParameter("ripples", _rippleParams);
	}

	// ==================== 菜单逻辑 ====================

	private void OnContinue() {
		Log.Info("继续游戏");
		var cfg = new ConfigFile();
		if (cfg.Load(SETTINGS_PATH) == Error.Ok) {
			int value = cfg.GetValue("archive", "level", 0).AsInt32();
			if (value >= 0 && value < Level.Length && !string.IsNullOrEmpty(Level[value])) {
				GetTree().ChangeSceneToFile(Level[value]);
				return;
			}
		}
		GetTree().ChangeSceneToFile(Level.First());
	}

	private void OnStart() {
		Log.Info("开始游戏 → 序章");
		GetTree().ChangeSceneToFile(Level.First());
	}

	private void OnSetting() {
		var scence = ResourceLoader.Load("res://Scenes/setting.tscn") as PackedScene;
		AddChild(scence.Instantiate<Control>());
	}

	private void OnBlind() {
		Log.Info("进入第二关 · 盲视");
		GetTree().ChangeSceneToFile("res://Scenes/Level/blind.tscn");
	}

	private void OnQuit() {
		Log.Info("退出游戏");
		GetTree().Quit();
	}

	private void OnToggleLanguage() {
		string current = TranslationServer.GetLocale();
		string next = current == "zh" ? "en" : "zh";
		TranslationServer.SetLocale(next);
		SaveLanguagePreference(next);
		// Godot 会自动刷新所有静态控件的翻译，
		// 但语言按钮的文字需要手动更新（因为它显示“将要切换到的语言”）
		RefreshLanguageButton();
	}

	private void RefreshLanguageButton() {
		// 根据当前语言，按钮显示“将要切换到的语言”
		bool isEnglish = TranslationServer.GetLocale() == "en";
		_langButton.Text = isEnglish ? "中文" : "English";
	}

	private string LoadLanguagePreference() {
		var cfg = new ConfigFile();
		if (cfg.Load(SETTINGS_PATH) == Error.Ok) {
			string value = cfg.GetValue("i18n", "language", "").AsString();
			return value;
		}
		return "";
	}

	private void SaveLanguagePreference(string locale) {
		var cfg = new ConfigFile();
		cfg.SetValue("i18n", "language", locale);
		cfg.Save(SETTINGS_PATH);
	}

	public override void _Process(double delta) {
		_t += (float)delta;
		UpdateShockwaves();
		QueueRedraw();
	}

	public override void _Draw() {
		var center = new Vector2(320, 170);
		for (int i = 0; i < 3; i++) {
			float phase = Mathf.PosMod(_t * 24f + i * 40f, 120f);
			float a = (1f - phase / 120f) * 0.35f;
			DrawArc(center, phase, 0f, Mathf.Tau, 48, new Color(0.5f, 0.85f, 1f, a), 2f, false);
		}
		DrawCircle(center, 3f, new Color(0.85f, 0.92f, 1f, 0.8f));
	}
}
