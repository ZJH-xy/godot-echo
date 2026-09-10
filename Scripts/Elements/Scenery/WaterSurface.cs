using Godot;
using System.Collections.Generic;

/// <summary>
/// 水面：非机关景物（纯 _Draw 视觉，不挡声波、无碰撞、不参与玩法）。
/// - 平时：水面线整体缓慢起伏，并有一条细波沿水面缓缓流动（高度取整像素，像素风）；
/// - 声波经过：自波前扫到水面的位置向两侧推开波纹——波峰沿水面推进、身后以
///   波长/衰减距离收尾，振幅按声波强度缩放并随时间整体衰减；
///   回声更弱（强度已含 EchoIntensityFactor=0.5），波纹偏紫（与声场 EchoColor 呼应）；
/// - 像素风：表面按像素列采样、高度取整到整像素，同高同亮段合并绘制（RLE），
///   波纹亮度按档量化（GlowLevels），不连续渐变。
/// 摆放：节点位置 = 水面矩形左上角（世界坐标），Size = 水面宽高；
/// 水面落水重生由 KillZone（危险区）负责，本脚本只做视觉；
/// 需要把水面压在玩家/荷叶下方时在场景里调 z_index（WaveField 已在 -20）。
/// </summary>
public partial class WaterSurface : Node2D {
	/// <summary>水面矩形尺寸（px）：节点位置为左上角。</summary>
	[Export] public Vector2 Size = new Vector2(224, 12);
	/// <summary>表面采样列宽（px）：波纹台阶的横向像素粒度。</summary>
	[Export] public float PixelCell = 2f;
	/// <summary>平时水面线整体起伏：幅度（px）/角速度（rad/s）。</summary>
	[Export] public float IdleBobAmp = 1f;
	[Export] public float IdleBobSpeed = 1.2f;
	/// <summary>平时细波：幅度（px）/波长（px）/相位速度（rad/s）。</summary>
	[Export] public float IdleWaveAmp = 0.8f;
	[Export] public float IdleWaveLen = 48f;
	[Export] public float IdleWaveSpeed = 2.4f;
	/// <summary>声波波纹：波峰沿水面的推进速度（px/s）。</summary>
	[Export] public float RippleSpeed = 170f;
	/// <summary>波纹最大振幅（px，按声波强度 0~1 缩放）。</summary>
	[Export] public float RippleAmp = 2.5f;
	/// <summary>波峰身后涟漪的波长（px）。</summary>
	[Export] public float RippleWavelength = 18f;
	/// <summary>波峰身后涟漪的衰减距离（px）。</summary>
	[Export] public float RippleFalloff = 30f;
	/// <summary>单次波纹的持续时间（秒）。</summary>
	[Export] public float RippleDuration = 1.4f;
	/// <summary>同时最多保留的波纹数（超出丢弃最旧）。</summary>
	[Export] public int MaxRipples = 12;
	/// <summary>波纹亮度分级数（像素化阶梯；1=恒亮无分档）。</summary>
	[Export] public int GlowLevels = 4;

	[Export] public Color SurfaceColor = new Color(0.6f, 0.85f, 0.98f);
	[Export] public Color RippleColor = new Color(0.82f, 0.68f, 1f);
	[Export] public Color BodyColor = new Color(0.2f, 0.42f, 0.62f);
	[Export] public Color DeepColor = new Color(0.12f, 0.28f, 0.44f);

	private sealed class Ripple {
		public float OriginX; // 水面上波纹的起点（局部 x）
		public float Age;
		public float Amp;
		public bool Echo;
	}

	private struct Sample {
		public int Y;    // 表面线相对基准的高度（像素，正=抬升）
		public int Lvl;  // 波纹强度分级（0..GlowLevels）
		public int Echo; // 回声占比分级（0..2）
	}

	private WaveField _field;
	private float _time;
	private readonly List<Ripple> _ripples = new();
	private readonly List<WaveField.WavePassEvent> _buffer = new();
	private readonly List<Sample> _samples = new();

	public override void _Ready() {
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
		Log.Debug($"水面[{Name}] 就绪 左上={GlobalPosition.Round()} 尺寸={Size}");
	}

	public override void _PhysicsProcess(double delta) {
		float dt = (float)delta;
		_time += dt;
		for (int i = _ripples.Count - 1; i >= 0; i--) {
			_ripples[i].Age += dt;
			if (_ripples[i].Age >= RippleDuration) {
				_ripples.RemoveAt(i);
			}
		}
		if (_field != null) {
			_buffer.Clear();
			_field.QueryPass(GlobalPosition + Size * 0.5f, GetInstanceId(), _buffer);
			foreach (var ev in _buffer) {
				SpawnRipple(ev);
			}
		}
		QueueRedraw();
	}

	/// <summary>声波扫过水面：在波源投影处（夹到水面内）激起一道波纹。</summary>
	private void SpawnRipple(WaveField.WavePassEvent ev) {
		if (MaxRipples <= 0) {
			return;
		}
		if (_ripples.Count >= MaxRipples) {
			_ripples.RemoveAt(0); // 丢弃最旧
		}
		float margin = PixelCell * 2f;
		float ox = Mathf.Clamp(ev.Origin.X - GlobalPosition.X, margin, Size.X - margin);
		_ripples.Add(new Ripple {
			OriginX = ox,
			Age = 0f,
			Amp = Mathf.Clamp(ev.Intensity, 0.05f, 1f) * RippleAmp,
			Echo = ev.Phase == WaveField.WavePhase.Echo
		});
	}

	/// <summary>某像素列的表面线高度与波纹亮度/回声占比（高度已取整像素）。</summary>
	private void SampleAt(float x, out int y, out int lvl, out int echo) {
		float h = IdleBobAmp * Mathf.Sin(_time * IdleBobSpeed)
			+ IdleWaveAmp * Mathf.Sin(Mathf.Tau * (x / IdleWaveLen) + _time * IdleWaveSpeed);
		float hit = 0f;
		float echoHit = 0f;
		foreach (var r in _ripples) {
			float d = Mathf.Abs(x - r.OriginX);
			float front = RippleSpeed * r.Age; // 波峰已推进的距离
			if (d >= front) {
				continue;
			}
			float behind = front - d; // 波峰身后距离：涟漪按此取样
			float fade = Mathf.Max(0f, 1f - r.Age / RippleDuration);
			float c = fade * Mathf.Exp(-behind / RippleFalloff);
			h += r.Amp * c * Mathf.Sin(Mathf.Tau * behind / RippleWavelength);
			hit += c;
			if (r.Echo) {
				echoHit += c;
			}
		}
		y = Mathf.RoundToInt(h);
		int levels = Mathf.Max(1, GlowLevels);
		// 波纹强度分级（像素化亮度阶梯：按 hit 强度取 0..levels 档）
		lvl = Mathf.RoundToInt(Mathf.Clamp(hit * 0.85f, 0f, 1f) * levels);
		echo = hit > 0.001f ? Mathf.RoundToInt(Mathf.Clamp(echoHit / hit, 0f, 1f) * 2f) : 0;
	}

	public override void _Draw() {
		if (Size.X <= 0f || Size.Y <= 0f) {
			return;
		}
		float cell = Mathf.Max(1f, PixelCell);
		int cols = Mathf.Max(1, Mathf.CeilToInt(Size.X / cell));
		_samples.Clear();
		for (int c = 0; c < cols; c++) {
			SampleAt((c + 0.5f) * Size.X / cols, out int y, out int lvl, out int echo);
			_samples.Add(new Sample { Y = y, Lvl = lvl, Echo = echo });
		}
		float colW = Size.X / cols;
		int runStart = 0;
		for (int c = 1; c <= cols; c++) {
			if (c < cols && _samples[c].Y == _samples[runStart].Y
				&& _samples[c].Lvl == _samples[runStart].Lvl
				&& _samples[c].Echo == _samples[runStart].Echo) {
				continue;
			}
			DrawRun(runStart * colW, (c - runStart) * colW, _samples[runStart]);
			runStart = c;
		}
		// 底部深水带：固定，不随表面起伏（水体总有这层深色）
		DrawRect(new Rect2(0, Size.Y - 2f, Size.X, 2f), DeepColor);
	}

	private void DrawRun(float x, float w, Sample s) {
		float surfY = -s.Y; // 高度取整像素后转屏幕坐标（正=抬升 即向上）
		float bodyTop = surfY + 1f;
		if (bodyTop < Size.Y) {
			DrawRect(new Rect2(x, bodyTop, w, Size.Y - bodyTop), BodyColor);
		}
		var line = SurfaceColor.Lerp(RippleColor, s.Echo * 0.5f);
		float levels = Mathf.Max(1f, GlowLevels);
		line = line.Lerp(new Color(1f, 1f, 1f), Mathf.Min(0.35f, (s.Lvl / levels) * 0.35f));
		DrawRect(new Rect2(x, surfY, w, 1f), line);
	}
}
