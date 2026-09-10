using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 全局音频管理（autoload：Scenes/AudioManager.tscn，节点名 AudioManager）：
/// - BGM：单个 AudioStreamPlayer，循环播放，支持淡入淡出与暂停/恢复；
/// - SE：AudioStreamPlayer 池（16 个），多音效同时播放，全忙时抢占最早的一个；
/// - Voice：单个 AudioStreamPlayer，语音/旁白独占，新语音打断旧语音。
/// 三条管线分别走 <b>BGM / SE / Voice 总线</b>（default_bus_layout.tres），全部汇入 Master，
/// 因此设置面板的“游戏音量”（Settings.ApplyVolume 作用于 Master）依然是总音量。
/// 资源路径：Assets/Audio/{bgm,se,voice}/&lt;文件名&gt;，扩展名可省略（见 ResourceResolver）。
/// 用法：AudioManager.Instance.PlaySe("jump")；AudioManager.Instance.PlayBgm("theme", fadeIn: 1f)。
/// </summary>
public partial class AudioManager : Node {
	public const int SePoolSize = 16;
	/// <summary>背景音乐总线名。</summary>
	public const string BgmBus = "BGM";
	/// <summary>音效总线名。</summary>
	public const string SeBus = "SE";
	/// <summary>语音总线名。</summary>
	public const string VoiceBus = "Voice";

	/// <summary>全局实例（注册为 autoload 后可用；未注册时为 null）。</summary>
	public static AudioManager Instance { get; private set; }

	private AudioStreamPlayer _bgmPlayer;
	private AudioStreamPlayer _voicePlayer;
	private readonly List<AudioStreamPlayer> _sePool = new();
	private readonly Dictionary<string, AudioStream> _streamCache = new();
	private readonly List<(Tween Tween, TaskCompletionSource<bool> Tcs)> _pending = new();
	private int _seIndex;
	private string _currentBgm = "";
	private bool _bgmLoop;
	private bool _bgmLoopHandlerAttached;
	private bool _initialized;
	private TaskCompletionSource<bool> _voiceTcs;

	/// <summary>当前 BGM 逻辑名（如 "theme"），空表示无。</summary>
	public string CurrentBgm => _currentBgm;

	public override void _Ready() {
		Instance = this;
		Init();
	}

	public override void _ExitTree() {
		if (Instance == this) {
			Instance = null;
		}
	}

	/// <summary>初始化（幂等）：确保 BGM/SE/Voice 总线存在并创建三条管线的播放器。</summary>
	public void Init() {
		if (_initialized) {
			return;
		}
		if (!IsInsideTree()) {
			Log.Error("AudioManager 必须在场景树中初始化：请把它注册为 autoload（Scenes/AudioManager.tscn）");
			return;
		}
		EnsureBuses();
		_bgmPlayer = CreatePlayer("BgmPlayer", BgmBus);
		_voicePlayer = CreatePlayer("VoicePlayer", VoiceBus);
		for (int i = 0; i < SePoolSize; i++) {
			_sePool.Add(CreatePlayer($"SePlayer{i}", SeBus));
		}
		_voicePlayer.Finished += OnVoiceFinished; // 仅订阅一次
		_initialized = true;
		Log.Debug($"音频管理就绪：BGM/SE(池 {SePoolSize})/Voice，总线 {BgmBus}/{SeBus}/{VoiceBus}");
	}

	/// <summary>
	/// 确保 BGM/SE/Voice 总线存在：默认由 default_bus_layout.tres 提供（含全部总线，勿删）；
	/// 万一布局缺失，这里按需补建并汇入 Master，保证三路管线都能出声。
	/// </summary>
	private static void EnsureBuses() {
		foreach (string bus in new[] { BgmBus, SeBus, VoiceBus }) {
			if (AudioServer.GetBusIndex(bus) >= 0) {
				continue;
			}
			int idx = AudioServer.BusCount;
			AudioServer.AddBus(idx);
			AudioServer.SetBusName(idx, bus);
			AudioServer.SetBusSend(idx, "Master");
			Log.Warn($"音频总线缺失，已自动补建：{bus}（建议在 default_bus_layout.tres 中维护）");
		}
	}

	private AudioStreamPlayer CreatePlayer(string name, string bus) {
		AudioStreamPlayer player = new() {
			Name = name,
			Bus = bus,
		};
		AddChild(player);
		return player;
	}

	// ==================== BGM ====================

	/// <summary>
	/// 播放/切换 BGM。file 为 Assets/Audio/bgm/ 下的逻辑名（扩展名可省略）。
	/// 切换时自动先淡出旧曲。返回的任务在淡入完成后结束。
	/// </summary>
	public async Task PlayBgmAsync(string file, bool loop = true, float volume = 1f, float fadeIn = 0f, float fadeOut = 0.5f) {
		if (!EnsureInit() || string.IsNullOrWhiteSpace(file)) {
			return;
		}
		if (_currentBgm == file && _bgmPlayer.Playing) {
			return;
		}

		// 打断当前 BGM（带淡出）
		if (_bgmPlayer.Playing && fadeOut > 0.0001f) {
			await FadeOutAndStopAsync(_bgmPlayer, fadeOut);
		} else {
			_bgmPlayer.Stop();
		}

		AudioStream stream = ResolveStream("bgm", file);
		if (stream == null) {
			return;
		}
		SetStreamLoop(stream, loop);
		_bgmLoop = loop;
		if (!_bgmLoopHandlerAttached) {
			// 兜底循环：导入时未勾选 loop 的流播完会触发 finished，这里重播；
			// 若在导入设置里勾了 loop（推荐），流本身不会结束，此处理器不会触发
			_bgmPlayer.Finished += OnBgmFinished;
			_bgmLoopHandlerAttached = true;
		}
		_bgmPlayer.Stream = stream;
		_bgmPlayer.PitchScale = 1f;
		_currentBgm = file;

		float targetDb = Mathf.LinearToDb(Mathf.Clamp(volume, 0f, 1f));
		_bgmPlayer.VolumeDb = fadeIn > 0.0001f ? -80f : targetDb;
		_bgmPlayer.Play();
		Log.Debug($"BGM 播放 {file} 循环={loop} 音量={volume:F2} 淡入={fadeIn}s");

		if (fadeIn > 0.0001f) {
			await FadeAsync(_bgmPlayer, targetDb, fadeIn);
		}
	}

	/// <summary>播放 BGM（不等待淡入完成，关卡脚本可直接调用）。</summary>
	public void PlayBgm(string file, bool loop = true, float volume = 1f, float fadeIn = 0f, float fadeOut = 0.5f) {
		_ = PlayBgmAsync(file, loop, volume, fadeIn, fadeOut);
	}

	/// <summary>停止 BGM（带淡出）。</summary>
	public async Task StopBgmAsync(float fadeOut = 0.5f) {
		if (!EnsureInit()) {
			return;
		}
		if (!_bgmPlayer.Playing) {
			_currentBgm = "";
			_bgmLoop = false;
			return;
		}

		_bgmLoop = false; // 先清循环标志，防止 Stop 触发 finished 时重播
		if (fadeOut > 0.0001f) {
			await FadeOutAndStopAsync(_bgmPlayer, fadeOut);
		} else {
			_bgmPlayer.Stop();
		}
		_currentBgm = "";
	}

	/// <summary>停止 BGM（不等待淡出完成）。</summary>
	public void StopBgm(float fadeOut = 0.5f) {
		_ = StopBgmAsync(fadeOut);
	}

	/// <summary>BGM 是否正在播放。</summary>
	public bool IsBgmPlaying => _bgmPlayer != null && _bgmPlayer.Playing;

	/// <summary>暂停 BGM（保留进度）。</summary>
	public void PauseBgm() {
		if (_bgmPlayer is { Playing: true }) {
			_bgmPlayer.StreamPaused = true;
		}
	}

	/// <summary>恢复 BGM。</summary>
	public void ResumeBgm() {
		if (_bgmPlayer != null) {
			_bgmPlayer.StreamPaused = false;
		}
	}

	/// <summary>调整 BGM 音量（不打断播放）。</summary>
	public async Task SetBgmVolumeAsync(float volume, float fade = 0.3f) {
		if (!EnsureInit()) {
			return;
		}
		float targetDb = Mathf.LinearToDb(Mathf.Clamp(volume, 0f, 1f));
		if (fade > 0.0001f && _bgmPlayer.Playing) {
			await FadeAsync(_bgmPlayer, targetDb, fade);
		} else {
			_bgmPlayer.VolumeDb = targetDb;
		}
	}

	// ==================== SE ====================

	/// <summary>
	/// 播放音效（非阻塞，池中取空闲播放器，全忙时抢占最早的一个）。
	/// file 为 Assets/Audio/se/ 下的逻辑名（扩展名可省略）。
	/// </summary>
	public void PlaySe(string file, float volume = 1f, float pitch = 1f) {
		if (!EnsureInit() || string.IsNullOrWhiteSpace(file)) {
			return;
		}
		try {
			AudioStream stream = ResolveStream("se", file);
			if (stream == null) {
				return;
			}
			AudioStreamPlayer player = GetFreeSePlayer();
			player.Stream = stream;
			player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volume * .6f, 0f, 1f));
			player.PitchScale = Mathf.Clamp(pitch, 0.05f, 4f);
			player.Play();
		} catch (Exception e) {
			Log.Error($"SE 播放失败 {file}", e);
		}
	}

	private AudioStreamPlayer GetFreeSePlayer() {
		foreach (AudioStreamPlayer p in _sePool) {
			if (!p.Playing) {
				return p;
			}
		}
		// 全部占用：抢占最早播放的
		AudioStreamPlayer oldest = _sePool[_seIndex];
		_seIndex = (_seIndex + 1) % _sePool.Count;
		return oldest;
	}

	/// <summary>停止所有 SE。</summary>
	public void StopAllSe() {
		foreach (AudioStreamPlayer p in _sePool) {
			p.Stop();
		}
	}

	// ==================== Voice ====================

	/// <summary>
	/// 播放语音（独占：打断旧语音）。file 为 Assets/Audio/voice/ 下的逻辑名。
	/// 返回的任务在语音播放结束时完成（被打断时也会完成）。
	/// </summary>
	public Task PlayVoiceAsync(string file, float volume = 1f) {
		TaskCompletionSource<bool> tcs = new();
		if (!EnsureInit() || string.IsNullOrWhiteSpace(file)) {
			tcs.TrySetResult(true);
			return tcs.Task;
		}
		try {
			AudioStream stream = ResolveStream("voice", file);
			if (stream == null) {
				tcs.TrySetResult(true);
				return tcs.Task;
			}
			_voicePlayer.Stop();
			_voicePlayer.Stream = stream;
			_voicePlayer.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volume, 0f, 1f));
			_voicePlayer.PitchScale = 1f;
			_voicePlayer.Play();
			_voiceTcs = tcs;
		} catch (Exception e) {
			Log.Error($"Voice 播放失败 {file}", e);
			tcs.TrySetResult(true);
		}
		return tcs.Task;
	}

	/// <summary>停止语音（被打断的等待任务会完成）。</summary>
	public void StopVoice() {
		if (_voicePlayer == null) {
			return;
		}
		_voicePlayer.Stop();
		OnVoiceFinished();
	}

	/// <summary>语音是否正在播放。</summary>
	public bool IsVoicePlaying => _voicePlayer != null && _voicePlayer.Playing;

	private void OnBgmFinished() {
		// 防御：仅在循环标志开启且有曲目时重播（Stop 触发 finished 时不重播）
		if (_bgmLoop && !string.IsNullOrEmpty(_currentBgm) && _bgmPlayer.Stream != null) {
			_bgmPlayer.Play();
		}
	}

	private void OnVoiceFinished() {
		_voiceTcs?.TrySetResult(true);
		_voiceTcs = null;
	}

	// ==================== 通用 ====================

	/// <summary>
	/// 让音频流自身循环（BGM 用）：即使导入设置里没勾 loop，也能无缝循环，
	/// 不依赖 <see cref="OnBgmFinished"/> 的重播兜底（重播会有几毫秒接缝）。
	/// </summary>
	private static void SetStreamLoop(AudioStream stream, bool loop) {
		switch (stream) {
			case AudioStreamOggVorbis ogg:
				ogg.Loop = loop;
				break;
			case AudioStreamMP3 mp3:
				mp3.Loop = loop;
				break;
			case AudioStreamWav wav:
				wav.LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled;
				break;
		}
	}

	/// <summary>
	/// 取音频流：按逻辑名从 Assets/Audio/&lt;kind&gt;/ 加载（扩展名可省略，见 ResourceResolver）；
	/// 找不到则打日志并返回 null（调用方静默降级，不影响玩法）。
	/// </summary>
	private AudioStream ResolveStream(string kind, string file) {
		string path = ResourceResolver.ResolveAudio(kind, file);
		if (path == null) {
			Log.Warn($"{kind} 音频未找到: {file}（应在 {ResourceResolver.AudioRoot}/{kind}/ 下）");
			return null;
		}
		return LoadStream(path);
	}

	/// <summary>加载并缓存音频流（res:// 路径，走 ResourceLoader：.ogg/.wav/.mp3 均按导入结果加载）。</summary>
	private AudioStream LoadStream(string path) {
		if (_streamCache.TryGetValue(path, out AudioStream cached)) {
			return cached;
		}
		AudioStream stream;
		try {
			stream = ResourceLoader.Load<AudioStream>(path);
		} catch (Exception e) {
			Log.Error($"音频加载失败 {path}", e);
			return null;
		}
		if (stream == null) {
			Log.Error($"音频加载失败 {path}（资源未导入？）");
			return null;
		}
		_streamCache[path] = stream;
		return stream;
	}

	/// <summary>淡出并停止播放器。</summary>
	private async Task FadeOutAndStopAsync(AudioStreamPlayer player, float duration) {
		await FadeAsync(player, -80f, duration);
		player.Stop();
	}

	/// <summary>渐变到目标音量（dB）。</summary>
	private Task FadeAsync(AudioStreamPlayer player, float targetDb, float duration) {
		TaskCompletionSource<bool> tcs = new();
		if (duration <= 0.0001f || !player.Playing) {
			player.VolumeDb = targetDb;
			tcs.TrySetResult(true);
			return tcs.Task;
		}
		Tween tween = CreateTween();
		_pending.Add((tween, tcs));
		tween.TweenProperty(player, "volume_db", targetDb, duration);
		tween.Finished += () => {
			_pending.RemoveAll(p => p.Tween == tween);
			tcs.TrySetResult(true);
		};
		return tcs.Task;
	}

	/// <summary>停止全部音频并终止淡入淡出，释放音频缓存。</summary>
	public void StopAll() {
		foreach ((Tween tween, TaskCompletionSource<bool> tcs) in _pending) {
			if (tween.IsValid()) {
				tween.Kill();
			}
			tcs.TrySetResult(true);
		}
		_pending.Clear();

		_bgmLoop = false; // 先清循环标志再停止
		_bgmPlayer?.Stop();
		_voicePlayer?.Stop();
		StopAllSe();
		OnVoiceFinished();
		_currentBgm = "";
		_streamCache.Clear();
	}

	/// <summary>确保已初始化；未成功时打一条错误并返回 false（调用方直接降级返回）。</summary>
	private bool EnsureInit() {
		if (!_initialized) {
			Init();
		}
		if (!_initialized) {
			Log.Error("AudioManager 未初始化：请确认已注册 autoload（Scenes/AudioManager.tscn）");
			return false;
		}
		return true;
	}
}
