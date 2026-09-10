using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AudioGen;

/// <summary>
/// 《步步高声》占位音频生成器（离线工具，不参与游戏构建）：
/// - 音效：程序化合成的短音，写成 Assets/Audio/se/*.wav；
/// - BGM：用一个小型音序器（和声进行 + 低音 + 琶音 + 旋律 + 鼓）谱写有段落起伏的曲子，
///   每关一首，写成 Assets/Audio/bgm/*.ogg（有 ffmpeg 时转 OGG，否则退回 WAV）。
/// 噪声用固定随机种子，重复生成结果一致。
/// 用法：
///   dotnet run --project Tools/AudioGen             # 自动定位仓库根 → Assets/Audio
///   dotnet run --project Tools/AudioGen -- &lt;输出根&gt;  # 指定输出目录
///   dotnet run --project Tools/AudioGen -- --wav     # 不转 OGG，全部输出 WAV
/// </summary>
internal static class Program {
	private const int MixRate = 22050;
	private const float Attack = 0.004f;
	private const float Release = 0.012f;
	/// <summary>OGG 转码质量（libvorbis -q:a，单声道）。</summary>
	private const string OggQuality = "2";
	/// <summary>BGM 归一化目标峰值：多声部叠加容易过载，统一压到这个峰值（只衰减不提升），
	/// 既保证不削波，又让各曲响度接近、给音效留出余量。</summary>
	private const float TargetPeak = 0.55f;

	private enum Wave {
		Sine,
		Triangle,
		Square,
		Noise
	}

	// ==================== 数据结构 ====================

	private sealed class Item {
		public string Kind;      // "se" / "bgm"
		public string Name;
		public bool Loop;
		public float[] Samples;
		public bool Ogg;         // 是否尝试转 OGG
		public string Note = "";
	}

	/// <summary>一段 4 小节乐句：和声 + 各声部在 8 分音符网格上的模式。</summary>
	private sealed class Phrase {
		public string[] Chords;      // 每小节一个和弦
		public string Mel = "";      // 32 格旋律：数字=音阶级数，'-'休止，'='延音
		public string Arp = "";      // 琶音触发（8 格重复 / 32 格全长）
		public string Kick = "";
		public string Snare = "";
		public string Hat = "";
		public float Gain = 1f;      // 段落动态
		public float BassOct = 0.5f; // 低音八度系数
		public float Pad;            // >0 时铺长音和弦（音量）
		public bool BassHalf;        // true=每小节两次低音（有推动感）
		public bool HarmThird;       // 旋律下加三度（合唱/加厚）
	}

	/// <summary>一首曲子：音阶 + 音色 + 若干乐句（顺序即段落：起、承、转、合）。</summary>
	private sealed class Track {
		public string Name;
		public string Note = "";
		public float Bpm = 100f;
		public float[] Scale = Array.Empty<float>();
		public Wave MelWave = Wave.Square;
		public Wave ArpWave = Wave.Triangle;
		public Wave BassWave = Wave.Triangle;
		public float MelGain = 0.5f;
		public float ArpGain = 0.38f;
		public float BassGain = 0.5f;
		public float DrumGain = 0.45f;
		public readonly List<Phrase> Phrases = new();
	}

	// ==================== 入口 ====================

	public static int Main(string[] args) {
		bool forceWav = Array.IndexOf(args, "--wav") >= 0;
		string outRoot = null;
		foreach (string a in args) {
			if (!a.StartsWith("--")) {
				outRoot = Path.GetFullPath(a);
			}
		}
		outRoot ??= Path.Combine(FindRepoRoot(), "Assets", "Audio");

		string seDir = Path.Combine(outRoot, "se");
		string bgmDir = Path.Combine(outRoot, "bgm");
		Directory.CreateDirectory(seDir);
		Directory.CreateDirectory(bgmDir);

		bool useOgg = !forceWav && HasFfmpeg();

		var items = new List<Item>();
		items.AddRange(BuildSes());
		items.AddRange(BuildBgms());

		Console.WriteLine($"输出目录：{outRoot}");
		Console.WriteLine($"BGM 编码：{(useOgg ? "OGG（libvorbis q" + OggQuality + "，经 ffmpeg）" : "WAV（未找到 ffmpeg 或指定 --wav）")}");
		Console.WriteLine();
		Console.WriteLine($"{"类型",-4} {"名称",-12} {"时长",8} {"峰值",6} {"大小",9}  文件");
		long total = 0;
		foreach (Item item in items) {
			string dir = item.Kind == "se" ? seDir : bgmDir;
			string wav = Path.Combine(dir, item.Name + ".wav");
			WriteWav(wav, item.Samples, item.Loop);
			string final = wav;
			if (item.Ogg && useOgg) {
				string ogg = Path.Combine(dir, item.Name + ".ogg");
				if (TryEncodeOgg(wav, ogg)) {
					File.Delete(wav);
					final = ogg;
				}
			}
			long size = new FileInfo(final).Length;
			total += size;
			string rel = final.Substring(outRoot.Length).TrimStart(Path.DirectorySeparatorChar);
			Console.WriteLine($"{item.Kind,-4} {item.Name,-12} {item.Samples.Length / (float)MixRate,7:F2}s {Peak(item.Samples),6:F2} {size / 1024f,7:F1}KB  {rel}  {item.Note}");
		}
		Console.WriteLine();
		Console.WriteLine($"共 {items.Count} 个文件，合计 {total / 1024f / 1024f:F2} MB");
		return 0;
	}

	private static string FindRepoRoot() {
		foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }) {
			var dir = new DirectoryInfo(start);
			while (dir != null) {
				if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) {
					return dir.FullName;
				}
				dir = dir.Parent;
			}
		}
		return Directory.GetCurrentDirectory();
	}

	private static bool HasFfmpeg() {
		try {
			using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { UseShellExecute = false, CreateNoWindow = true });
			p.WaitForExit();
			return p.ExitCode == 0;
		} catch (Exception) {
			return false;
		}
	}

	/// <summary>wav → ogg（libvorbis，单声道）。ffmpeg 缺失/失败返回 false（保留 WAV）。</summary>
	private static bool TryEncodeOgg(string wavPath, string oggPath) {
		try {
			var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
			psi.ArgumentList.Add("-y");
			psi.ArgumentList.Add("-loglevel");
			psi.ArgumentList.Add("error");
			psi.ArgumentList.Add("-i");
			psi.ArgumentList.Add(wavPath);
			psi.ArgumentList.Add("-c:a");
			psi.ArgumentList.Add("libvorbis");
			psi.ArgumentList.Add("-q:a");
			psi.ArgumentList.Add(OggQuality);
			psi.ArgumentList.Add("-ac");
			psi.ArgumentList.Add("1");
			psi.ArgumentList.Add(oggPath);
			using var proc = Process.Start(psi);
			proc.WaitForExit();
			return proc.ExitCode == 0 && File.Exists(oggPath) && new FileInfo(oggPath).Length > 0;
		} catch (Exception e) {
			Console.WriteLine($"  （OGG 转码失败，保留 WAV：{e.Message}）");
			return false;
		}
	}

	// ==================== 音效 ====================

	private static List<Item> BuildSes() => new() {
		Se("wave_omni", Tone(320f, 150f, 0.22f, Wave.Sine, 0.55f), "全向发声"),
		Se("wave_fan", Tone(560f, 280f, 0.16f, Wave.Triangle, 0.5f), "扇形发声"),
		Se("echo", Echo(), "回声收敛"),
		Se("bullet_time", Tone(420f, 170f, 0.30f, Wave.Sine, 0.35f), "进入子弹时间"),
		Se("detector", Arp(new[] { 880f, 1318.5f }, 0.09f, Wave.Sine, 0.42f), "检测器触发"),
		Se("door", Tone(150f, 105f, 0.30f, Wave.Square, 0.32f), "门开/关"),
		Se("checkpoint", Arp(new[] { 659.25f, 987.77f }, 0.11f, Wave.Sine, 0.42f), "存档点激活"),
		Se("pickup", Arp(new[] { 1046.5f, 1568f }, 0.05f, Wave.Sine, 0.38f), "刷新点拾取"),
		Se("bounce", Tone(200f, 620f, 0.13f, Wave.Triangle, 0.42f), "弹力方块弹开"),
		Se("death", Death(), "玩家死亡"),
		Se("respawn", Arp(new[] { 330f, 494f, 659f }, 0.07f, Wave.Sine, 0.38f), "传送回复活点"),
		Se("complete", Arp(new[] { 523.25f, 659.25f, 783.99f, 1046.5f }, 0.12f, Wave.Triangle, 0.4f), "抵达终点")
	};

	private static Item Se(string name, float[] samples, string note) =>
		new() { Kind = "se", Name = name, Loop = false, Samples = samples, Note = note };

	/// <summary>单音（f1 &gt; 0 时在音内滑到 f1）。</summary>
	private static float[] Tone(float f0, float f1, float duration, Wave wave, float volume) {
		var buf = new float[(int)(duration * MixRate) + 1];
		AddNote(buf, f0, f1, 0f, duration, wave, volume);
		return buf;
	}

	/// <summary>依次起音的短琶音（上行音符）。</summary>
	private static float[] Arp(float[] freqs, float step, Wave wave, float volume) {
		float duration = step * freqs.Length + 0.22f;
		var buf = new float[(int)(duration * MixRate) + 1];
		for (int i = 0; i < freqs.Length; i++) {
			float vol = volume * (i == freqs.Length - 1 ? 1.15f : 0.9f);
			AddNote(buf, freqs[i], 0f, i * step, step + 0.18f, wave, vol);
		}
		return buf;
	}

	private static float[] Echo() {
		var buf = new float[(int)(0.75f * MixRate) + 1];
		AddNote(buf, 660f, 0f, 0f, 0.70f, Wave.Sine, 0.38f);
		AddNote(buf, 990f, 0f, 0.06f, 0.60f, Wave.Sine, 0.15f);
		return buf;
	}

	private static float[] Death() {
		var buf = new float[(int)(0.45f * MixRate) + 1];
		AddNote(buf, 420f, 90f, 0f, 0.40f, Wave.Square, 0.30f);
		AddNote(buf, 0f, 0f, 0.02f, 0.22f, Wave.Noise, 0.16f);
		return buf;
	}

	// ==================== BGM ====================

	private static List<Item> BuildBgms() {
		var list = new List<Item>();
		// 主菜单：舒缓的 4 小节循环（沿用最初的曲子）
		list.Add(Bgm("menu", SimpleLoop(menu: true), "主菜单（舒缓循环）"));
		// 通用兜底：未知场景使用
		list.Add(Bgm("level", SimpleLoop(menu: false), "通用兜底循环"));
		// 五关：每关一首有段落起伏的曲子
		list.AddRange(new[] {
			Bgm("prologue", Render(Prologue()), "序章·初啼：明亮、好奇，24 小节"),
			Bgm("echo", Render(Echo_Level()), "第一关·余音：空灵、呼应，20 小节"),
			Bgm("blind", Render(Blind()), "第二关·盲视：低沉、紧绷，20 小节"),
			Bgm("mute", Render(Mute()), "第三关·失声：极简、冷冽，20 小节"),
			Bgm("chorus", Render(Chorus()), "第四关·合唱：层层加厚、高潮，24 小节")
		});
		return list;
	}

	private static Item Bgm(string name, float[] samples, string note) =>
		new() { Kind = "bgm", Name = name, Loop = true, Samples = samples, Ogg = true, Note = note };

	/// <summary>最初的简单循环（主菜单 / 兜底用，4 小节 Am-F-C-G）。</summary>
	private static float[] SimpleLoop(bool menu) {
		float bpm = menu ? 76f : 104f;
		float beat = 60f / bpm;
		float total = 4 * beat * 4f;
		var buf = new float[(int)(total * MixRate) + 1];
		float[] roots = { 110f, 87.31f, 130.81f, 98f };
		float[][] chords = {
			new[] { 440f, 523.25f, 659.25f },
			new[] { 349.23f, 440f, 523.25f },
			new[] { 523.25f, 659.25f, 783.99f },
			new[] { 392f, 493.88f, 587.33f }
		};
		for (int bar = 0; bar < 4; bar++) {
			float t0 = bar * beat * 4f;
			AddNote(buf, roots[bar] * 0.5f, 0f, t0, beat * 1.8f, Wave.Triangle, menu ? 0.18f : 0.22f);
			AddNote(buf, roots[bar] * 0.5f, 0f, t0 + beat * 2f, beat * 1.8f, Wave.Triangle, menu ? 0.15f : 0.19f);
			for (int i = 0; i < 8; i++) {
				float f = chords[bar][(i + bar) % 3];
				if (!menu && i % 4 == 2) {
					f *= 2f;
				}
				float vol = (menu ? 0.09f : 0.11f) * (i % 4 == 0 ? 1.3f : 0.85f);
				AddNote(buf, f, 0f, t0 + i * beat * 0.5f, beat * 0.46f, menu ? Wave.Sine : Wave.Triangle, vol);
			}
			if (!menu) {
				Kick(buf, t0 + beat * 2f, 0.05f);
				Hat(buf, t0 + beat * 3.5f, 0.035f);
			}
		}
		return buf;
	}

	// ---------- 五关曲子 ----------

	/// <summary>序章·初啼：C 大调五声，明亮好奇；起（琶音引子）→承（主题）→转（高潮加鼓）→合（回归）。</summary>
	private static Track Prologue() {
		var t = new Track {
			Name = "prologue",
			Bpm = 96f,
			Scale = Notes("C4 D4 E4 G4 A4 C5 D5 E5 G5 A5"),
			MelWave = Wave.Square,
			ArpWave = Wave.Sine,
			MelGain = 0.42f,
			ArpGain = 0.30f,
			BassGain = 0.42f,
			DrumGain = 0.34f
		};
		t.Phrases.Add(new Phrase {      // 起：只有琶音与低音，像晨光
			Chords = new[] { "C", "G", "Am", "F" },
			Arp = "x---x---", Pad = 0.05f, Gain = 0.65f
		});
		t.Phrases.Add(new Phrase {      // 起：琶音走动
			Chords = new[] { "C", "G", "F", "G" },
			Arp = "x-x-x-x-", Pad = 0.05f, Gain = 0.72f
		});
		t.Phrases.Add(new Phrase {      // 承：主题进入
			Chords = new[] { "C", "G", "Am", "F" },
			Arp = "x-x-x-x-", Pad = 0.05f, BassHalf = true, Gain = 0.85f,
			Mel = "0---2---3-------2---0-----------"
		});
		t.Phrases.Add(new Phrase {      // 承：答句
			Chords = new[] { "C", "G", "F", "G" },
			Arp = "x-x-x-x-", BassHalf = true, Gain = 0.9f,
			Mel = "3---4---5---4---3---2---0---2---"
		});
		t.Phrases.Add(new Phrase {      // 转：高潮，鼓组全开
			Chords = new[] { "F", "G", "C", "Am" },
			Arp = "x-x-x-x-", Kick = "x---x---", Snare = "----x---", Hat = "x-x-x-x-",
			BassHalf = true, Gain = 1f,
			Mel = "5---6---7---6---5---4---3-4-5---", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 合：回归宁静
			Chords = new[] { "F", "G", "C", "C" },
			Arp = "x---x---", Pad = 0.06f, Gain = 0.68f,
			Mel = "4---3---2-------0---------------"
		});
		return t;
	}

	/// <summary>第一关·余音：A 小调，留白大、长音多，乐句高八度呼应（回声意象）。</summary>
	private static Track Echo_Level() {
		var t = new Track {
			Name = "echo",
			Bpm = 80f,
			Scale = Notes("A3 B3 C4 D4 E4 F4 G4 A4 B4 C5"),
			MelWave = Wave.Sine,
			ArpWave = Wave.Triangle,
			MelGain = 0.5f,
			ArpGain = 0.24f,
			BassGain = 0.4f,
			DrumGain = 0.3f
		};
		t.Phrases.Add(new Phrase {      // 起：空灵长音
			Chords = new[] { "Am", "F", "C", "G" },
			Pad = 0.07f, Gain = 0.6f,
			Mel = "7-------5-------4-------2-------"
		});
		t.Phrases.Add(new Phrase {      // 起→呼应：同动机换尾音
			Chords = new[] { "Am", "F", "Dm", "G" },
			Pad = 0.07f, Gain = 0.68f,
			Mel = "9-------7-------5-----------0---"
		});
		t.Phrases.Add(new Phrase {      // 承：走动起来
			Chords = new[] { "Am", "F", "C", "G" },
			Arp = "x---x-x-", Pad = 0.06f, BassHalf = true, Gain = 0.88f,
			Mel = "0-2-4---5---4-2-0---2-----------"
		});
		t.Phrases.Add(new Phrase {      // 转：抬升
			Chords = new[] { "Dm", "Am", "F", "G" },
			Arp = "x-x-x-x-", Kick = "x-------", Hat = "----x---", BassHalf = true, Gain = 1f,
			Mel = "4---5---7---5---4---2---0---2---", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 合：消散
			Chords = new[] { "Am", "F", "Am", "G" },
			Pad = 0.08f, Gain = 0.55f,
			Mel = "9-------7-------5-------2-------"
		});
		return t;
	}

	/// <summary>第二关·盲视：D 小调，低沉压抑、半音靠近制造不安；起→紧张→最强→退去。</summary>
	private static Track Blind() {
		var t = new Track {
			Name = "blind",
			Bpm = 72f,
			Scale = Notes("D4 E4 F4 G4 A4 A#4 C5 D5 E5 F5"),
			MelWave = Wave.Triangle,
			ArpWave = Wave.Sine,
			MelGain = 0.5f,
			ArpGain = 0.22f,
			BassGain = 0.52f,
			DrumGain = 0.32f
		};
		t.Phrases.Add(new Phrase {      // 起：低音脉动 + 稀疏高音点
			Chords = new[] { "Dm", "Dm", "Bb", "Bb" },
			Arp = "x-------", Pad = 0.07f, Gain = 0.6f,
			Mel = "--------3-------6-------5-------"
		});
		t.Phrases.Add(new Phrase {      // 起：靠近半音，开始不安
			Chords = new[] { "Gm", "Gm", "A", "A" },
			Arp = "x---x---", Pad = 0.05f, Gain = 0.8f,
			Mel = "3---3-4-5-------4---3-----------"
		});
		t.Phrases.Add(new Phrase {      // 转：最强，鼓组进入
			Chords = new[] { "Dm", "Bb", "F", "A" },
			Arp = "x-x-x-x-", Kick = "x---x---", Snare = "----x---", Hat = "x---x---",
			BassHalf = true, Gain = 1f,
			Mel = "6---5---4---5---6-------8-------", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 转：下行回落
			Chords = new[] { "Dm", "Bb", "Gm", "A" },
			Arp = "x-x-x---", Kick = "x---x---", BassHalf = true, Gain = 0.9f,
			Mel = "8---7---5---4---3---2---1-------"
		});
		t.Phrases.Add(new Phrase {      // 合：退回黑暗
			Chords = new[] { "Dm", "Dm", "Dm", "A" },
			Pad = 0.09f, Gain = 0.5f,
			Mel = "--------5-------3---------------"
		});
		return t;
	}

	/// <summary>第三关·失声：E 小调，极简冷冽、大量留白（“没有回声”的空）。</summary>
	private static Track Mute() {
		var t = new Track {
			Name = "mute",
			Bpm = 76f,
			Scale = Notes("E4 F4 G4 A4 B4 C5 D5 E5 F5 G5"),
			MelWave = Wave.Sine,
			ArpWave = Wave.Sine,
			MelGain = 0.46f,
			ArpGain = 0.2f,
			BassGain = 0.46f,
			DrumGain = 0.28f
		};
		t.Phrases.Add(new Phrase {      // 起：孤立单音
			Chords = new[] { "Em", "Em", "Am", "Am" },
			Pad = 0.05f, Gain = 0.55f,
			Mel = "0-------3-------2-------0-------"
		});
		t.Phrases.Add(new Phrase {      // 承：低音进入
			Chords = new[] { "C", "C", "D", "D" },
			Arp = "x-------", Pad = 0.05f, Gain = 0.75f,
			Mel = "2---4---5-------4---2-----------"
		});
		t.Phrases.Add(new Phrase {      // 转：密集但压抑
			Chords = new[] { "Em", "C", "Am", "D" },
			Arp = "x-x-x-x-", Kick = "x---x---", Hat = "----x---",
			BassHalf = true, Gain = 0.95f,
			Mel = "2-2-4-4-5---4-2-0-------2---4---"
		});
		t.Phrases.Add(new Phrase {      // 转：上行后落回
			Chords = new[] { "Em", "Am", "C", "D" },
			Arp = "x-x-x---", Kick = "x-------", BassHalf = true, Gain = 0.85f,
			Mel = "7---5---4---2---0-------4-------"
		});
		t.Phrases.Add(new Phrase {      // 合：留白
			Chords = new[] { "Em", "Em", "Em", "Em" },
			Pad = 0.07f, Gain = 0.45f,
			Mel = "0---------------0---------------"
		});
		return t;
	}

	/// <summary>第四关·合唱：F 大调，主题→加厚→大高潮（加三度=多声部）→收束。</summary>
	private static Track Chorus() {
		var t = new Track {
			Name = "chorus",
			Bpm = 104f,
			Scale = Notes("F4 G4 A4 A#4 C5 D5 E5 F5 G5 A5"),
			MelWave = Wave.Square,
			ArpWave = Wave.Triangle,
			MelGain = 0.4f,
			ArpGain = 0.32f,
			BassGain = 0.46f,
			DrumGain = 0.4f
		};
		t.Phrases.Add(new Phrase {      // 起：主题呈示
			Chords = new[] { "F", "C", "Dm", "Bb" },
			Arp = "x-x-x-x-", Kick = "x---x---", Hat = "x-x-x-x-",
			BassHalf = true, Pad = 0.05f, Gain = 0.8f,
			Mel = "0---2---4---2---5---4---2---0---"
		});
		t.Phrases.Add(new Phrase {      // 承：答句
			Chords = new[] { "F", "C", "Bb", "C" },
			Arp = "x-x-x-x-", Kick = "x---x---", Snare = "----x---", Hat = "x-x-x-x-",
			BassHalf = true, Gain = 0.9f,
			Mel = "4---5---7---5---4---2---0-------"
		});
		t.Phrases.Add(new Phrase {      // 承：加厚（三度和声）
			Chords = new[] { "Dm", "Bb", "F", "C" },
			Arp = "x-x-x-x-", Kick = "x---x---", Snare = "----x---", Hat = "x-x-x-x-",
			BassHalf = true, Gain = 0.95f,
			Mel = "7---7---5---5---4---4---2---2---", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 转：往上推
			Chords = new[] { "Dm", "Bb", "Gm", "C" },
			Arp = "x-x-x-x-", Kick = "x---x---", Snare = "----x---", Hat = "x-xxx-x-",
			BassHalf = true, Gain = 1f,
			Mel = "9---7---5---7---8---7---4---5---", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 合：大高潮，全声部
			Chords = new[] { "F", "Bb", "C", "F" },
			Arp = "x-xxx-x-", Kick = "x--xx---", Snare = "----x---", Hat = "x-xxx-x-",
			BassHalf = true, Gain = 1.05f,
			Mel = "5-5-7-7-8-8-7---5-5-4-4-5-------", HarmThird = true
		});
		t.Phrases.Add(new Phrase {      // 合：收束回主题
			Chords = new[] { "Bb", "C", "F", "F" },
			Arp = "x---x---", Pad = 0.07f, Gain = 0.72f,
			Mel = "4---5---4---2---0---------------"
		});
		return t;
	}

	// ==================== 音序渲染 ====================

	private static float[] Render(Track t) {
		float beat = 60f / t.Bpm;
		float eighth = beat * 0.5f;
		int totalBars = 0;
		foreach (Phrase p in t.Phrases) {
			totalBars += p.Chords.Length;
		}
		var buf = new float[(int)(totalBars * beat * 4f * MixRate) + 1];
		int barIndex = 0;
		foreach (Phrase p in t.Phrases) {
			int phraseBar = barIndex;
			// ---- 低音 / 铺底：逐小节 ----
			for (int b = 0; b < p.Chords.Length; b++) {
				float t0 = (phraseBar + b) * beat * 4f;
				float[] chord = ChordOf(p.Chords[b]);
				float bass = chord[0] * p.BassOct;
				if (p.BassHalf) {
					AddNote(buf, bass, 0f, t0, beat * 1.8f, t.BassWave, t.BassGain * p.Gain);
					AddNote(buf, bass, 0f, t0 + beat * 2f, beat * 1.8f, t.BassWave, t.BassGain * p.Gain);
				} else {
					AddNote(buf, bass, 0f, t0, beat * 3.7f, t.BassWave, t.BassGain * p.Gain);
				}
				if (p.Pad > 0f) {
					foreach (float n in chord) {
						AddNote(buf, n, 0f, t0, beat * 3.85f, Wave.Sine, p.Pad * p.Gain);
					}
				}
			}
			// ---- 琶音：8 格模式按小节重复（也支持 32 格全长） ----
			if (!string.IsNullOrEmpty(p.Arp)) {
				for (int i = 0; i < 32; i++) {
					if (p.Arp[i % p.Arp.Length] != 'x') {
						continue;
					}
					float[] chord = ChordOf(p.Chords[(i / 8) % p.Chords.Length]);
					float n = chord[(i / 2) % chord.Length] * (i % 8 >= 4 ? 2f : 1f);
					AddNote(buf, n, 0f, (phraseBar * 4 + i * 0.5f) * beat, eighth * 0.92f, t.ArpWave, t.ArpGain * p.Gain);
				}
			}
			// ---- 旋律：32 格，'=' 延音；HarmThird 时下加三度（合唱感） ----
			if (!string.IsNullOrEmpty(p.Mel)) {
				for (int i = 0; i < 32; i++) {
					char c = p.Mel[i];
					if (c < '0' || c > '9') {
						continue;
					}
					int len = 1;
					while (i + len < 32 && p.Mel[i + len] == '=') {
						len++;
					}
					int deg = c - '0';
					float dur = len * eighth * 0.94f;
					AddNote(buf, t.Scale[deg], 0f, (phraseBar * 4 + i * 0.5f) * beat, dur, t.MelWave, t.MelGain * p.Gain);
					if (p.HarmThird && deg >= 2) {
						AddNote(buf, t.Scale[deg - 2], 0f, (phraseBar * 4 + i * 0.5f) * beat, dur, Wave.Sine, t.MelGain * p.Gain * 0.45f);
					}
				}
			}
			// ---- 鼓组 ----
			Pattern(p.Kick, (i) => Kick(buf, (phraseBar * 4 + i * 0.5f) * beat, t.DrumGain * p.Gain));
			Pattern(p.Snare, (i) => Snare(buf, (phraseBar * 4 + i * 0.5f) * beat, t.DrumGain * p.Gain * 0.8f));
			Pattern(p.Hat, (i) => Hat(buf, (phraseBar * 4 + i * 0.5f) * beat, t.DrumGain * p.Gain * 0.35f));
			barIndex += p.Chords.Length;
		}
		// 归一化：削波会让音色发破，统一压到目标峰值（只衰减，不提升）
		float peak = Peak(buf);
		if (peak > TargetPeak) {
			float k = TargetPeak / peak;
			for (int i = 0; i < buf.Length; i++) {
				buf[i] *= k;
			}
		}
		return buf;
	}

	private static void Pattern(string pattern, Action<int> hit) {
		if (string.IsNullOrEmpty(pattern)) {
			return;
		}
		for (int i = 0; i < 32; i++) {
			if (pattern[i % pattern.Length] == 'x') {
				hit(i);
			}
		}
	}

	// ---- 打击乐 ----

	/// <summary>底鼓：110→45Hz 下滑正弦，短促。</summary>
	private static void Kick(float[] buf, float start, float vol) {
		int offset = (int)(start * MixRate);
		int count = (int)(0.14f * MixRate);
		double phase = 0.0;
		for (int i = 0; i < count; i++) {
			int idx = offset + i;
			if (idx < 0 || idx >= buf.Length) {
				continue;
			}
			float p = i / (float)count;
			float f = 110f + (45f - 110f) * p;
			phase += f / (double)MixRate;
			buf[idx] += MathF.Sin((float)(phase - Math.Floor(phase)) * MathF.Tau) * vol * MathF.Exp(-9f * p);
		}
	}

	/// <summary>军鼓：噪声 + 200Hz 体声。</summary>
	private static void Snare(float[] buf, float start, float vol) {
		int offset = (int)(start * MixRate);
		int count = (int)(0.10f * MixRate);
		double phase = 0.0;
		for (int i = 0; i < count; i++) {
			int idx = offset + i;
			if (idx < 0 || idx >= buf.Length) {
				continue;
			}
			float p = i / (float)count;
			phase += 200f / (double)MixRate;
			float tone = MathF.Sin((float)(phase - Math.Floor(phase)) * MathF.Tau) * 0.4f;
			buf[idx] += (tone + (Rng.NextSingle() * 2f - 1f)) * vol * MathF.Exp(-14f * p);
		}
	}

	/// <summary>踩镲：极短噪声。</summary>
	private static void Hat(float[] buf, float start, float vol) {
		int offset = (int)(start * MixRate);
		int count = (int)(0.03f * MixRate);
		for (int i = 0; i < count; i++) {
			int idx = offset + i;
			if (idx < 0 || idx >= buf.Length) {
				continue;
			}
			float p = i / (float)count;
			buf[idx] += (Rng.NextSingle() * 2f - 1f) * vol * MathF.Exp(-28f * p);
		}
	}

	// ==================== 音高 / 和弦 ====================

	/// <summary>音名 → 频率（如 C4、A#3、Eb5；A4=440）。</summary>
	private static float F(string name) {
		int semi = name[0] switch {
			'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => 0
		};
		int i = 1;
		if (i < name.Length && (name[i] == '#' || name[i] == 'b')) {
			semi += name[i] == '#' ? 1 : -1;
			i++;
		}
		int octave = int.Parse(name.Substring(i));
		int midi = (octave + 1) * 12 + semi;
		return 440f * MathF.Pow(2f, (midi - 69) / 12f);
	}

	private static float[] Notes(string names) {
		string[] parts = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var result = new float[parts.Length];
		for (int i = 0; i < parts.Length; i++) {
			result[i] = F(parts[i]);
		}
		return result;
	}

	/// <summary>和弦音（升序，[0] 即低音根音）。</summary>
	private static float[] ChordOf(string name) => name switch {
		"Am" => new[] { F("A3"), F("C4"), F("E4") },
		"C" => new[] { F("C4"), F("E4"), F("G4") },
		"F" => new[] { F("F3"), F("A3"), F("C4") },
		"G" => new[] { F("G3"), F("B3"), F("D4") },
		"Em" => new[] { F("E3"), F("G3"), F("B3") },
		"Dm" => new[] { F("D3"), F("F3"), F("A3") },
		"Bb" => new[] { F("A#3"), F("D4"), F("F4") },
		"Gm" => new[] { F("G3"), F("A#3"), F("D4") },
		"Eb" => new[] { F("D#3"), F("G3"), F("A#3") },
		"D" => new[] { F("D3"), F("F#3"), F("A3") },
		"A" => new[] { F("A3"), F("C#4"), F("E4") },
		"Bm" => new[] { F("B3"), F("D4"), F("F#4") },
		_ => new[] { F("A3"), F("C4"), F("E4") }
	};

	// ==================== 合成基元 ====================

	private static float[] Empty(int seconds) => new float[(int)(seconds * MixRate) + 1];

	private static void AddNote(float[] buf, float f0, float f1, float start, float duration, Wave wave, float volume) {
		int offset = (int)(start * MixRate);
		int count = (int)(duration * MixRate);
		double phase = 0.0;
		for (int i = 0; i < count; i++) {
			int idx = offset + i;
			if (idx < 0 || idx >= buf.Length) {
				continue;
			}
			float t = i / (float)count;
			float freq = f1 > 0f ? f0 + (f1 - f0) * t : f0;
			phase += freq / (double)MixRate;
			buf[idx] += Sample(wave, phase) * volume * Envelope(i / (float)MixRate, duration);
		}
	}

	private static float Sample(Wave wave, double phase) {
		if (wave == Wave.Noise) {
			return Rng.NextSingle() * 2f - 1f;
		}
		float p = (float)(phase - Math.Floor(phase));
		switch (wave) {
			case Wave.Square:
				return p < 0.5f ? 0.7f : -0.7f;
			case Wave.Triangle:
				return 4f * MathF.Abs(p - 0.5f) - 1f;
			default:
				return MathF.Sin(p * MathF.Tau);
		}
	}

	/// <summary>快起 + 指数衰减 + 尾部短释放（避免爆音与咔哒声）。</summary>
	private static float Envelope(float t, float duration) {
		if (t < Attack) {
			return t / Attack;
		}
		if (t > duration - Release) {
			return MathF.Max(0f, (duration - t) / Release);
		}
		float rel = (t - Attack) / MathF.Max(0.001f, duration - Attack);
		return MathF.Exp(-3.5f * rel);
	}

	private static float Peak(float[] samples) {
		float peak = 0f;
		foreach (float s in samples) {
			peak = MathF.Max(peak, MathF.Abs(s));
		}
		return peak;
	}

	/// <summary>固定种子：噪声成分可复现，重复生成不会产生二进制差异。</summary>
	private static readonly Random Rng = new(20260910);

	// ==================== WAV 输出 ====================

	/// <summary>写 16bit 单声道 PCM WAV；loop 时附 smpl 块（Godot 导入器“Detect From WAV”可识别循环点）。</summary>
	private static void WriteWav(string path, float[] samples, bool loop) {
		int frames = samples.Length;
		int dataBytes = frames * 2;
		int smplBytes = loop ? 36 + 24 : 0;
		using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
		using var w = new BinaryWriter(fs, Encoding.ASCII);
		Tag(w, "RIFF");
		w.Write(36 + dataBytes + (loop ? 8 + smplBytes : 0));
		Tag(w, "WAVE");
		Tag(w, "fmt ");
		w.Write(16);
		w.Write((short)1);
		w.Write((short)1);
		w.Write(MixRate);
		w.Write(MixRate * 2);
		w.Write((short)2);
		w.Write((short)16);
		Tag(w, "data");
		w.Write(dataBytes);
		for (int i = 0; i < frames; i++) {
			w.Write((short)Math.Clamp(samples[i] * 32767f, -32767f, 32767f));
		}
		if (loop) {
			Tag(w, "smpl");
			w.Write(smplBytes);
			w.Write(0);
			w.Write(0);
			w.Write((int)(1_000_000_000.0 / MixRate));
			w.Write(60);
			w.Write(0);
			w.Write(0);
			w.Write(0);
			w.Write(1);
			w.Write(0);
			w.Write(0);
			w.Write(0);
			w.Write(0);
			w.Write(frames - 1);
			w.Write(0);
			w.Write(0);
		}
	}

	private static void Tag(BinaryWriter w, string tag) => w.Write(Encoding.ASCII.GetBytes(tag));
}
