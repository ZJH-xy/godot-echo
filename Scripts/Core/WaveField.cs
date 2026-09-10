using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// 关卡声场：负责声波的扩散计算与机关触发查询、波形绘制与手柄三段式震动。
/// 用法：关卡中放置一个属于组 "wave_field" 的本节点，按关卡配置导出参数。
/// 计算说明：
/// - 全向波/扇形波都用射线可见性（被墙阻挡后不绕行，声波穿过缝隙后保持缝隙形状）；
/// - 声波放大器（组 "wave_amplifier"）：声波射线穿过放大区时，其后路径强度乘以该区倍率
///   （每穿过一次放大一次，可与反射墙组合做多段放大），强度可超过玩家最大 1.0；
///   放大后的强声波（强度 > 1.0）可推动玩家（Player.WavePushVelocity 等参数）——
///   每单元记录“最大倍率射线”的倍率/到达路径长度/传播方向供推动查询；
/// - 回声（仅全向波）：**始终是一个真正的圆**——前缘是从 `MaxRadius` 向内回缩的完整圆环，
///   到达某单元的时刻只由它到声源的欧氏半径决定（不是发声波形状的倒放）。
///   默认（EchoMode=Blocked）**回声被地形阻挡**：只有与声源有视线（发声段射线到得了）的单元
///   才会被圆环扫到，墙后/遮挡区永不回应，圆环在那里断开；
///   切到 EchoMode=Through 则回到最旧的行为（完整圆环回缩、完全不看地形）。
///   两种模式都沿用同一套前缘回缩（半径从 MaxRadius 递减到 0）逻辑：绘制/辉光/盲视轮廓/触发查询；
///   回声不受放大器影响（无射线路径）；
/// - 像素风绘制：波前为单格亮环（带弱外晕），范围内辉光为短衰减尾迹，
///   声波经过后随时间衰减，回声触发前已消散。
/// - 大地图：开销与"地图大小"解耦，几十万格的关卡照常跑——
///   每道声波只按「射程窗口」（声源 ± 单段最大射程）分配逐格数组（见 <see cref="LocalGrid"/>），
///   每帧的辉光/轮廓/绘制也只遍历声波实际覆盖的包围盒；
///   实心网格只在有碰撞体的候选矩形内逐格物理查询（见 <see cref="CollectSolidCandidateRects"/>），
///   空地一格不查。网格单元数不再有上限，只对异常大的范围打警告（提示可能有离群瓦片）。
/// </summary>
public partial class WaveField : Node2D {
	[Export] public Rect2 Bounds = new Rect2(0, 0, 1280, 360);
	/// <summary>网格单元尺寸（px）。越小精度越高；4px 相比 8px 提升一倍精度。</summary>
	[Export] public float CellSize = 4f;
	[Export] public float WaveSpeed = 340f;
	[Export] public float MaxRadius = 735f;
	/// <summary>回声传播模型：Blocked=完整圆环回缩但被地形阻挡（默认）；Through=完整圆环回缩、不看地形（旧行为）。</summary>
	[Export] public EchoModel EchoMode = EchoModel.Blocked;
	/// <summary>关卡回声延迟：声波扩散到最大半径后，等待这段时间回声才开始收拢。</summary>
	[Export] public float EchoDelay = 0.8f;
	/// <summary>回声强度 = 发声强度 * 该系数（回声弱于发声）。</summary>
	[Export] public float EchoIntensityFactor = 0.5f;
	/// <summary>扇形声波碰到反射墙（组 reflect_wall）时，单条射线最多反射的次数。
	/// 超过预算后沿当前方向继续直行衰减，避免波前在多次反射通道中“凭空消失”。</summary>
	[Export] public int MaxFanReflections = 8;
	[Export] public bool DrawWash = true;
	/// <summary>盲视模式：声波覆盖到的地形边界单元格被勾勒（配合 BlindReveal 节点在雾层上方绘制）。</summary>
	[Export] public bool BlindMode = false;
	/// <summary>盲视模式下轮廓的淡出时长（秒）。</summary>
	[Export] public float RevealDuration = 6f;
	[Export] public uint SolidMask = 1;
	[Export] public Color OutboundColor = new Color(0.75f, 0.92f, 1f, 0.9f);
	[Export] public Color EchoColor = new Color(0.85f, 0.65f, 1f, 0.75f);
	[Export] public Color WashColor = new Color(0.5f, 0.8f, 1f, 0.07f);
	/// <summary>声波经过后，范围内辉光的衰减时长（秒）：短尾迹，回声触发前已消散。</summary>
	[Export] public float AfterglowDecay = 0.8f;
	/// <summary>范围内辉光的亮度分级档数（像素化）：辉光随距离/时间衰减时按档跳变，
	/// 而不是连续渐变。数值越小越"方块"（如 4~6 档像素风；1 档=熄灭前恒亮）。</summary>
	[Export] public int GlowLevels = 5;

	public enum WavePhase {
		Outbound,
		Echo
	}

	/// <summary>回声传播模型。</summary>
	public enum EchoModel {
		/// <summary>完整圆环从外向内回缩，但**被地形阻挡**：只有与声源有视线的单元会被扫到，
		/// 墙后/遮挡区永不回应（圆环在那里断开）。</summary>
		Blocked,
		/// <summary>完整圆环回缩，完全不看地形：墙后照样亮（最旧的观感）。</summary>
		Through
	}

	/// <summary>一次“波前经过某元素”的事件。</summary>
	public sealed class WavePassEvent {
		public WavePhase Phase;
		public float Intensity;
		public Vector2 Origin;
		/// <summary>波前在该局的传播方向：发声段为该单元最大倍率射线的方向，回声段指向声源。</summary>
		public Vector2 Dir;
		/// <summary>波前到达该元素位置的理论模拟时刻（毫秒，相对引擎启动）。
		/// 供需要比较两路声音到达先后的机关（如共鸣装置）使用；上报帧可能晚于该时刻 ≤1 物理帧。</summary>
		public float SimTimeMs;
	}

	// ---- 盲视模式供 BlindReveal 读取的网格信息 ----

	public int GridCols => _cols;
	public int GridRows => _rows;
	public int GridCellCount => _cellCount;
	public float GridCellSize => CellSize;
	public Rect2 GridBounds => Bounds;

	/// <summary>边界掩码：bit0 左侧贴实体、bit1 右侧、bit2 上方、bit3 下方。</summary>
	public byte BoundaryAt(int idx) {
		return _boundary == null ? (byte)0 : _boundary[idx];
	}

	/// <summary>该边界单元格当前的轮廓亮度（0~1，随时间衰减）。</summary>
	public float RevealAt(int idx) {
		return _reveal == null ? 0f : _reveal[idx];
	}

	/// <summary>
	/// 单道声波的局部网格窗口：逐格数组只覆盖「这道声波能到达的矩形」，
	/// 于是内存与每帧开销只与射程有关，与地图大小无关（大地图不再受网格单元数限制）。
	///
	/// - 窗口 = 全局网格 ∩ (声源 ± 单段最大射程)：全向波与无反射墙的扇形波，射线被 MaxRadius 封顶，
	///   窗口必然是射程方框；有反射墙的扇形波，反射段可能折返到射程之外，窗口退化为整张全局网格
	///   （保守但简单——反射关都是小关卡，整图也小）；
	/// - 窗口外的单元一律按「未覆盖」读取（距离 -1 / 辉光 0 / 倍率 1）；
	/// - <see cref="MinCol"/> 等记录射线**实际走过**的范围，<see cref="IterCol0"/> 等是每帧遍历
	///   （辉光/轮廓/绘制）用的范围：扇形波用实际范围，全向波因为回声是完整圆环、
	///   可能扫到发声段没走到的单元，所以用整个窗口。
	/// </summary>
	private sealed class LocalGrid {
		public int Col0, Row0, Cols, Rows, CellCount;
		/// <summary>每单元声波首次到达的路径长度（px，从声源起算）；-1=未到达。</summary>
		public float[] Dist;
		/// <summary>每单元"反射段"的到达路径长度（px）：反射折返后再次扫过的最早到达；
		/// -1=无再次扫过；无反射墙的关卡为 null。</summary>
		public float[] ReflDist;
		/// <summary>每单元声波强度倍率（经声波放大器放大后 >1；未放大为 1）；无放大器为 null。</summary>
		public float[] AmpMax;
		/// <summary>达到 AmpMax 倍率的射线到达该单元的路径长度（px）：强声波推动玩家的触发时刻。</summary>
		public float[] AmpArrive;
		/// <summary>达到 AmpMax 倍率的射线在该单元的传播方向。</summary>
		public Vector2[] AmpDir;
		/// <summary>回声距离场（仅全向波）：回声前缘扫过该单元时的剩余距离（= 到声源的欧氏半径）；
		/// Blocked 模式下被地形挡住的单元为 -1；扇形波为 null。</summary>
		public float[] EchoDist;
		/// <summary>本声波专属的范围内辉光层（0~1）：被本声波覆盖的点亮，随后随时间衰减淡出。</summary>
		public float[] Glow;
		/// <summary>射线实际走过的最小/最大列（全局单元坐标，含端点）。</summary>
		public int MinCol = int.MaxValue, MaxCol = int.MinValue;
		/// <summary>射线实际走过的最小/最大行（全局单元坐标，含端点）。</summary>
		public int MinRow = int.MaxValue, MaxRow = int.MinValue;
		/// <summary>每帧遍历范围（全局单元坐标，含端点）：空范围时 IterCol1 &lt; IterCol0。</summary>
		public int IterCol0 = 1, IterCol1 = 0, IterRow0 = 1, IterRow1 = 0;

		public bool HasTouched => MaxCol >= MinCol && MaxRow >= MinRow;
		public bool HasIter => IterCol1 >= IterCol0 && IterRow1 >= IterRow0;

		public bool Contains(int col, int row) {
			return col >= Col0 && col < Col0 + Cols && row >= Row0 && row < Row0 + Rows;
		}

		public int Index(int col, int row) {
			return (row - Row0) * Cols + (col - Col0);
		}

		/// <summary>标记射线走过一个单元（用于统计实际范围，供每帧只遍历这块包围盒）。</summary>
		public void Touch(int col, int row) {
			if (col < MinCol) {
				MinCol = col;
			}
			if (col > MaxCol) {
				MaxCol = col;
			}
			if (row < MinRow) {
				MinRow = row;
			}
			if (row > MaxRow) {
				MaxRow = row;
			}
		}

		/// <summary>定下每帧遍历范围：全向波用整个窗口（回声是完整圆环），扇形波只用实际走过的范围。</summary>
		public void FinishIterRange(bool echoPossible) {
			if (echoPossible) {
				IterCol0 = Col0;
				IterCol1 = Col0 + Cols - 1;
				IterRow0 = Row0;
				IterRow1 = Row0 + Rows - 1;
			} else if (HasTouched) {
				IterCol0 = MinCol;
				IterCol1 = MaxCol;
				IterRow0 = MinRow;
				IterRow1 = MaxRow;
			} else {
				IterCol0 = 1;
				IterCol1 = 0;
				IterRow0 = 1;
				IterRow1 = 0;
			}
		}

		/// <summary>波身消失后只保留辉光层，其余逐格数组及早交给 GC。</summary>
		public void ReleaseHeavyFields() {
			Dist = null;
			ReflDist = null;
			AmpMax = null;
			AmpArrive = null;
			AmpDir = null;
			EchoDist = null;
		}
	}

	private sealed class WaveData {
		/// <summary>发声时刻的累计模拟时间（毫秒，随 Engine.TimeScale 缩放）。</summary>
		public double Start;
		public Vector2 Origin;
		public float Speed;
		/// <summary>回声腿的起始半径（= MaxRadius）：回声前缘由它递减到 0，整段回声时长 = Radius / Speed。</summary>
		public float Radius;
		public float Delay;
		public float Intensity;
		public float TOut;
		/// <summary>是否产生回声（扇形声波不产生回声）。</summary>
		public bool HasEcho;
		/// <summary>本道声波的局部窗口（距离场/辉光等逐格数组都在这里面）。</summary>
		public LocalGrid Grid;
		public Dictionary<ulong, byte> Delivered = new();
		public int Phase;
	}

	/// <summary>射线步进写入目标：局部窗口（距离场 + 放大器倍率信息）。</summary>
	private struct RaySink {
		public LocalGrid Grid;
		/// <summary>本波次射线到达的最大路径长度（px，含反射段）：用于延长扇形波生命周期（TOut）。</summary>
		public float MaxPath;
	}

	private int _cols;
	private int _rows;
	private int _cellCount;
	private bool[] _solid;
	private byte[] _boundary;
	private float[] _reveal;
	/// <summary>轮廓被点亮过的单元范围（全局列/行，含端点）：衰减只遍历这块，不扫整张网格。</summary>
	private int _revealCol0 = int.MaxValue, _revealCol1 = int.MinValue;
	private int _revealRow0 = int.MaxValue, _revealRow1 = int.MinValue;
	private bool _hasGlow;
	/// <summary>已结束声波移交的辉光残余层：波身消失后这些辉光继续透明淡出（不弹硬切）。</summary>
	private readonly List<LocalGrid> _fadingGlows = new();
	private bool _solidDirty = true;
	/// <summary>每个网格单元的放大器编号（-1=无放大器）；与 _ampFactors 组成放大区网格。</summary>
	private int[] _ampId;
	/// <summary>放大器倍率表（按 _ampId 索引）。</summary>
	private readonly List<float> _ampFactors = new();
	private readonly List<WaveData> _waves = new();
	/// <summary>累计模拟时间（毫秒，按缩放后的 delta 累加）：子弹时间等 Engine.TimeScale 变化会让声波同步减速/加速。</summary>
	private double _simTime;
	private RectangleShape2D _cellShape;
	private PhysicsShapeQueryParameters2D _query;
	private const int WashLevels = 40;
	/// <summary>全向波射线角间距（度）：最大半径处相邻射线间隔小于一个单元，保证无缺漏。</summary>
	private const float OmniRayDeg = 0.25f;
	/// <summary>实心网格预热：地形碰撞体（尤其 TileMapLayer 的象限静态体）要到物理帧才注册，
	/// 在它之前查询只会得到空地形。故先跳过 SolidWarmupSkipFrames 个物理帧再查询；
	/// 若查到的地形为空（还没注册完）就每物理帧重试，最多 SolidWarmupRetries 次。</summary>
	private const int SolidWarmupSkipFrames = 1;
	private const int SolidWarmupRetries = 3;
	/// <summary>还需跳过的物理帧数（预热用）。</summary>
	private int _solidWarmupLeft = SolidWarmupSkipFrames;
	/// <summary>剩余的预热重试次数（查到地形后清零，不再重算）。</summary>
	private int _solidRetriesLeft = SolidWarmupRetries;
	/// <summary>最近一次实心网格里判定为地形的单元数（0 说明查询没拿到地形，遮挡会失效）。</summary>
	private int _solidCells;
	/// <summary>网格单元数警告阈值：超过就提示地图里可能有离群瓦片。只是提示——
	/// 逐格数组按射程窗口分配、每帧只遍历声波覆盖范围、实心网格只查候选矩形，
	/// 所以地图大小不再决定开销，网格本身不再设单元数上限。</summary>
	private const int WarnCells = 1_000_000;
	/// <summary>网格单元数硬上限：只在明显异常（4px 格下 6400 万格 ≈ 32000×32000 px）时
	/// 放弃按地形扩展，兜住 int 溢出与极端内存占用。</summary>
	private const int MaxCells = 64_000_000;
	/// <summary>视差背景被统一压到的深度：比“世界层与声场”再往下这么多（仅用于 z 排序）。</summary>
	private const int BackdropDepth = 10;

	public override void _Ready() {
		Settings.Load(); // 直接运行关卡时也加载已保存的设置（震动强度等）
		AddToGroup("wave_field");
		// 视差背景（Parallax2D）默认与世界层同为 z=0，会盖住 z=-20 的声场：统一把背景压到底层
		PushBackdropsBehind();
		// 先按关卡地形校准网格范围：关卡完全可能整体位于负坐标（瓦片坐标本来就允许负值），
		// 只按场景里填的 Bounds 建网格会让那片区域"没有声波"（声波进不去、回声不亮、检测器收不到）
		FitBoundsToTerrain();
		RebuildGridMetrics();
		_cellShape = new RectangleShape2D { Size = new Vector2(CellSize * 0.9f, CellSize * 0.9f) };
		_query = new PhysicsShapeQueryParameters2D { Shape = _cellShape, CollisionMask = SolidMask, CollideWithBodies = true, CollideWithAreas = false };
		_simTime = Time.GetTicksMsec();
		// 实心网格**不能**在 _Ready 里算：TileMapLayer 的地形碰撞体（象限静态体）要到物理帧才注册，
		// 提前查询只会查到空地形、并把这个结果缓存下来（声波从此不再被地形挡住）。
		// 改由 _PhysicsProcess 预热重算（跳过 1 帧 + 空结果重试），查到地形即收工。
		_solidDirty = true;
		_solidWarmupLeft = SolidWarmupSkipFrames;
		_solidRetriesLeft = SolidWarmupRetries;
	}

	/// <summary>按当前 Bounds 重算网格尺寸；旧网格作废，等下一次 EnsureSolid 重建。</summary>
	private void RebuildGridMetrics() {
		_cols = Mathf.CeilToInt(Bounds.Size.X / CellSize);
		_rows = Mathf.CeilToInt(Bounds.Size.Y / CellSize);
		_cellCount = _cols * _rows;
		_solid = null;
		_boundary = null;
		_reveal = null; // 尺寸随网格变，作废重算（否则盲视轮廓会按旧尺寸读取）
		_revealCol0 = int.MaxValue;
		_revealCol1 = int.MinValue;
		_revealRow0 = int.MaxValue;
		_revealRow1 = int.MinValue;
		_solidCells = 0;
		_solidDirty = true;
	}

	/// <summary>
	/// 把网格 Bounds 扩展为「场景里填的 Bounds ∪ 关卡地形范围」：地形范围取场景中所有
	/// <see cref="TileMapLayer"/> 的已用单元格矩形（× 瓦片尺寸 × 图层变换），做法与
	/// 关卡脚本 LeaderManagement 设置相机限制一致。
	/// 关卡可以延伸到负坐标（y&lt;0 / x&lt;0）：此时若只按场景里填的 Bounds 建网格，
	/// 那片区域就没有网格单元，表现为"声波不进入 y 为负的区域"。
	/// </summary>
	/// <returns>Bounds 是否被扩展（供日志/需要重建网格时判断）</returns>
	private bool FitBoundsToTerrain() {
		Node root = GetTree().CurrentScene ?? GetParent();
		if (root == null) {
			return false;
		}
		Rect2 fitted = Bounds;
		bool found = false;
		AccumulateTilemapRects(root, ref fitted, ref found);
		if (!found) {
			return false;
		}
		fitted = fitted.Grow(CellSize * 2f); // 外扩两格，避免地形正好压在网格边界上
		if (fitted.Position.IsEqualApprox(Bounds.Position) && fitted.Size.IsEqualApprox(Bounds.Size)) {
			return false;
		}
		long cells = (long)Mathf.CeilToInt(fitted.Size.X / CellSize) * Mathf.CeilToInt(fitted.Size.Y / CellSize);
		if (cells > MaxCells) {
			Log.Warn($"地形范围异常大（{fitted} ≈ {cells} 格 > 硬上限 {MaxCells}），网格保持场景填写的 Bounds：" +
				"请检查瓦片图层里是否有离群的单元格（如误画在几千格外的一两格）");
			return false;
		}
		if (cells > WarnCells) {
			Log.Warn($"地形范围较大（{fitted} ≈ {cells} 格）：仍按地形扩展网格。" +
				"逐格数组已改为按声波射程窗口分配、实心网格只查候选矩形，地图再大也不会因此变慢；" +
				"但若地图其实没这么大，请检查瓦片图层里是否有离群的单元格");
		}
		Rect2 old = Bounds;
		Bounds = fitted;
		Log.Info($"声场网格已按地形扩展：{old} → {Bounds}");
		return true;
	}

	/// <summary>递归收集场景里 TileMapLayer 的世界矩形并入 rect。</summary>
	private static void AccumulateTilemapRects(Node node, ref Rect2 rect, ref bool found) {
		if (node is TileMapLayer layer && layer.TileSet != null) {
			Rect2I used = layer.GetUsedRect();
			if (used.Size.X > 0 && used.Size.Y > 0) {
				Vector2 tile = layer.TileSet.TileSize;
				Vector2 topLeft = layer.ToGlobal(new Vector2(used.Position.X * tile.X, used.Position.Y * tile.Y));
				Vector2 bottomRight = layer.ToGlobal(new Vector2(used.End.X * tile.X, used.End.Y * tile.Y));
				rect = rect.Merge(new Rect2(topLeft, Vector2.Zero).Expand(bottomRight));
				found = true;
			}
		}
		foreach (Node child in node.GetChildren()) {
			AccumulateTilemapRects(child, ref rect, ref found);
		}
	}

	/// <summary>
	/// 把场景里的视差背景（Parallax2D）压到声场与世界层之下，保证声波画在背景之上。
	/// 序章/第一关的声场刻意摆在 z=-20（画在地形之下当底光），而 Parallax2D 是普通 Node2D、
	/// 默认 z=0 且与地形同层——一叠上去背景就整个盖住声波（表现为“声波绘制在背景之下”）。
	/// 层次不逐关改场景维护，改由声场启动时统一把背景压到
	/// z = min(本节点 z, 0) − BackdropDepth（绝对 z，不受父节点影响）：
	/// 于是“视差背景 → 声场 → 世界层（地形/机关/玩家）”的顺序恒成立，
	/// 声场 z 在世界层之上时（盲视/失声/合唱关为 101）背景也照样只压到世界层之下。
	/// 只动 Parallax2D 自身的 z，不动地形与机关；同 z 的背景之间仍按场景树顺序叠放。
	/// </summary>
	private void PushBackdropsBehind() {
		Node root = GetTree().CurrentScene ?? GetParent();
		if (root == null) {
			return;
		}
		var backdrops = new List<Parallax2D>();
		CollectParallax2D(root, backdrops);
		if (backdrops.Count == 0) {
			return;
		}
		int backdropZ = Mathf.Min(ZIndex, 0) - BackdropDepth;
		foreach (var backdrop in backdrops) {
			backdrop.ZAsRelative = false; // 绝对 z：不受包裹它的 Node2D 影响
			backdrop.ZIndex = backdropZ;
		}
		Log.Debug($"视差背景 {backdrops.Count} 层已压到 z={backdropZ}（声场 z={ZIndex}）：声波绘制在背景之上");
	}

	/// <summary>递归收集场景里的视差背景（Parallax2D 自身即代表整层，其子节点随它一起排序）。</summary>
	private static void CollectParallax2D(Node node, List<Parallax2D> found) {
		if (node is Parallax2D parallax) {
			found.Add(parallax);
		}
		foreach (Node child in node.GetChildren()) {
			CollectParallax2D(child, found);
		}
	}

	/// <summary>地形（或门）发生变化时标记为脏，下次发声前重新计算阻挡。</summary>
	public void MarkSolidDirty() {
		_solidDirty = true;
	}

	/// <summary>
	/// 用物理查询预计算每个网格单元是否被实体地形占据。
	/// 只在「可能实心」的候选矩形内逐格查询（候选 = 关卡里所有遮挡碰撞形状的包围盒 ∪ 所有瓦片的矩形，
	/// 见 <see cref="CollectSolidCandidateRects"/>）——空地一格都不查，
	/// 于是重建开销只与地形/机关的面积有关，与地图大小无关（几十万格的大地图也能秒建）。
	/// </summary>
	public void EnsureSolid() {
		if (_solid != null && !_solidDirty) {
			return;
		}
		_solidDirty = false;
		_solid = new bool[_cellCount];
		_solidCells = 0;
		List<Rect2> candidates = CollectSolidCandidateRects();
		// 先把候选矩形覆盖的单元标出来（同一格被多个矩形覆盖只查一次），再逐格物理查询给最终答案
		var pending = new bool[_cellCount];
		int pendingCells = 0;
		foreach (Rect2 rect in candidates) {
			pendingCells += MarkRectCells(pending, rect);
		}
		var space = GetWorld2D().DirectSpaceState;
		for (int row = 0; row < _rows; row++) {
			int baseIdx = row * _cols;
			for (int col = 0; col < _cols; col++) {
				int i = baseIdx + col;
				if (!pending[i]) {
					continue;
				}
				_query.Transform = new Transform2D(0, CellCenter(col, row));
				bool hit = space.IntersectShape(_query, 1).Count > 0;
				_solid[i] = hit;
				if (hit) {
					_solidCells++;
				}
			}
		}
		Log.Debug($"实心网格重建：{_cols}×{_rows}={_cellCount} 格，候选={pendingCells} 格（{candidates.Count} 个矩形），" +
			$"地形={_solidCells} 格（遮挡掩码={SolidMask}）");
		// 计算每个空闲单元的“贴墙边界”掩码（盲视模式勾勒轮廓、回声源判定用）
		_boundary = new byte[_cellCount];
		for (int row = 0; row < _rows; row++) {
			for (int col = 0; col < _cols; col++) {
				int i = row * _cols + col;
				if (_solid[i]) {
					continue;
				}
				byte b = 0;
				if (col > 0 && _solid[i - 1]) {
					b |= 1;
				}
				if (col < _cols - 1 && _solid[i + 1]) {
					b |= 2;
				}
				if (row > 0 && _solid[i - _cols]) {
					b |= 4;
				}
				if (row < _rows - 1 && _solid[i + _cols]) {
					b |= 8;
				}
				_boundary[i] = b;
			}
		}
		RebuildAmpGrid();
	}

	/// <summary>
	/// 收集「可能实心」的世界矩形，供 <see cref="EnsureSolid"/> 只在这些矩形里逐格查询：
	/// - 所有 <see cref="TileMapLayer"/> 的已用瓦片矩形——瓦片的碰撞体是引擎内部建的象限静态体，
	///   拿不到节点，只能按瓦片定位（瓦片矩形 = 瓦片坐标 × 瓦片尺寸，与 <see cref="AccumulateTilemapRects"/> 同源）；
	/// - 场景里所有会遮挡声波的碰撞形状（CollisionShape2D / CollisionPolygon2D）的全局包围盒——
	///   门、反射墙、活动平台、弹力方块、StaticBody2D 地形都算在内。
	/// 这些只是候选（最终答案仍由逐格物理查询给出），所以宁可多收：多收只是多查几格，漏收才会漏地形。
	/// Area2D 与碰撞层不含遮挡掩码的碰撞体（感应区等）永远不会被查询命中，直接跳过。
	/// </summary>
	private List<Rect2> CollectSolidCandidateRects() {
		var rects = new List<Rect2>();
		Node root = GetTree().CurrentScene ?? GetParent();
		if (root != null) {
			CollectCandidateRects(root, rects);
		}
		return rects;
	}

	/// <summary>递归收集候选矩形（见 <see cref="CollectSolidCandidateRects"/>）。</summary>
	private void CollectCandidateRects(Node node, List<Rect2> rects) {
		switch (node) {
			case TileMapLayer layer when layer.TileSet != null:
				Vector2 tileSize = layer.TileSet.TileSize;
				foreach (Vector2I cell in layer.GetUsedCells()) {
					Vector2 topLeft = layer.ToGlobal(new Vector2(cell.X * tileSize.X, cell.Y * tileSize.Y));
					rects.Add(new Rect2(topLeft, tileSize));
				}
				break;
			case CollisionShape2D shape when shape.Shape != null && BlocksSound(shape):
				rects.Add(shape.GlobalTransform * shape.Shape.GetRect());
				break;
			case CollisionPolygon2D poly when poly.Polygon.Length > 0 && BlocksSound(poly):
				rects.Add(PolygonAabb(poly));
				break;
		}
		foreach (Node child in node.GetChildren()) {
			CollectCandidateRects(child, rects);
		}
	}

	/// <summary>该碰撞形状所属的碰撞体是否可能遮挡声波（不是 Area2D，且碰撞层命中遮挡掩码）。</summary>
	private bool BlocksSound(Node shapeNode) {
		Node n = shapeNode.GetParent();
		while (n != null) {
			if (n is Area2D) {
				return false; // 感应区不挡声波（查询用 CollideWithAreas=false）
			}
			if (n is CollisionObject2D body) {
				return (body.CollisionLayer & SolidMask) != 0;
			}
			n = n.GetParent();
		}
		return true; // 找不到宿主碰撞体：保守收进候选
	}

	/// <summary>CollisionPolygon2D 顶点的全局包围盒。</summary>
	private static Rect2 PolygonAabb(CollisionPolygon2D poly) {
		Transform2D xf = poly.GlobalTransform;
		Rect2 rect = new Rect2(xf * poly.Polygon[0], Vector2.Zero);
		for (int i = 1; i < poly.Polygon.Length; i++) {
			rect = rect.Expand(xf * poly.Polygon[i]);
		}
		return rect;
	}

	/// <summary>把世界矩形覆盖的网格单元标为候选（外扩一格防差一格），返回新标记的单元数。</summary>
	private int MarkRectCells(bool[] marked, Rect2 worldRect) {
		Rect2 r = worldRect.Grow(CellSize);
		int col0 = Mathf.Max(0, Mathf.FloorToInt((r.Position.X - Bounds.Position.X) / CellSize));
		int col1 = Mathf.Min(_cols - 1, Mathf.FloorToInt((r.End.X - Bounds.Position.X) / CellSize));
		int row0 = Mathf.Max(0, Mathf.FloorToInt((r.Position.Y - Bounds.Position.Y) / CellSize));
		int row1 = Mathf.Min(_rows - 1, Mathf.FloorToInt((r.End.Y - Bounds.Position.Y) / CellSize));
		int added = 0;
		for (int row = row0; row <= row1; row++) {
			int baseIdx = row * _cols;
			for (int col = col0; col <= col1; col++) {
				int i = baseIdx + col;
				if (!marked[i]) {
					marked[i] = true;
					added++;
				}
			}
		}
		return added;
	}

	/// <summary>
	/// 重建放大器网格：扫描组 "wave_amplifier" 的声波放大器节点，把其世界矩形覆盖的
	/// 单元编号写入 _ampId（倍率存 _ampFactors）。放大器为静态区域，随实心网格一起计算。
	/// 只遍历放大器矩形覆盖的单元（不扫整张网格），大地图也不拖慢。
	/// </summary>
	private void RebuildAmpGrid() {
		_ampId = null;
		_ampFactors.Clear();
		var amps = GetTree().GetNodesInGroup("wave_amplifier");
		if (amps.Count == 0) {
			return;
		}
		var rects = new List<Rect2>();
		foreach (var node in amps) {
			if (node is SoundAmplifier amp && amp.AmplifyFactor > 1f) {
				rects.Add(amp.WorldRect);
				_ampFactors.Add(amp.AmplifyFactor);
				Log.Debug($"声波放大器[{amp.Name}] 计入声场 矩形={amp.WorldRect} 倍率={amp.AmplifyFactor}");
			}
		}
		if (rects.Count == 0) {
			return;
		}
		_ampId = new int[_cellCount];
		Array.Fill(_ampId, -1);
		for (int i = 0; i < rects.Count; i++) {
			Rect2 r = rects[i];
			int col0 = Mathf.Max(0, Mathf.FloorToInt((r.Position.X - Bounds.Position.X) / CellSize));
			int col1 = Mathf.Min(_cols - 1, Mathf.FloorToInt((r.End.X - Bounds.Position.X) / CellSize));
			int row0 = Mathf.Max(0, Mathf.FloorToInt((r.Position.Y - Bounds.Position.Y) / CellSize));
			int row1 = Mathf.Min(_rows - 1, Mathf.FloorToInt((r.End.Y - Bounds.Position.Y) / CellSize));
			for (int row = row0; row <= row1; row++) {
				int baseIdx = row * _cols;
				for (int col = col0; col <= col1; col++) {
					int idx = baseIdx + col;
					// 与旧的逐格扫描同义：一个单元归属"第一个包含它的放大器"
					if (_ampId[idx] < 0 && r.HasPoint(CellCenter(col, row))) {
						_ampId[idx] = i;
					}
				}
			}
		}
	}

	public void EmitOmni(Vector2 origin, float intensity) {
		Emit(origin, intensity, Vector2.Right, 0f, false);
	}

	public void EmitFan(Vector2 origin, Vector2 dir, float halfAngleDeg, float intensity) {
		Emit(origin, intensity, dir.Normalized(), halfAngleDeg, true);
	}

	private void Emit(Vector2 origin, float intensity, Vector2 dir, float halfAngleDeg, bool fan) {
		EnsureSolid();
		int originIdx = WorldToIndex(origin);
		if (originIdx < 0 || _solid[originIdx]) {
			originIdx = FindFreeNear(origin);
		}
		Vector2 frontOrigin = originIdx >= 0 ? CellCenter(originIdx % _cols, originIdx / _cols) : origin;
		// 逐格数组只按「射程窗口」分配（不按整张网格），大地图也不会为一道声波分配几十万个单元
		bool reflectWalls = GetTree().GetNodesInGroup("reflect_wall").Count > 0;
		LocalGrid grid = CreateLocalGrid(frontOrigin, fan, reflectWalls);
		grid.Dist = new float[grid.CellCount];
		Array.Fill(grid.Dist, -1f);
		grid.Glow = new float[grid.CellCount];
		if (reflectWalls) {
			// 只有反射墙关卡才需要"反射段再次扫过"的场；否则省下整窗口数组
			grid.ReflDist = new float[grid.CellCount];
			Array.Fill(grid.ReflDist, -1f);
		}
		if (_ampFactors.Count > 0) {
			// 只有关卡里真有放大器才需要逐单元的倍率信息；否则省下整窗口数组（QueryPushAt/AmpAt 已判 null）
			grid.AmpMax = new float[grid.CellCount];
			Array.Fill(grid.AmpMax, 1f);
			grid.AmpArrive = new float[grid.CellCount];
			Array.Fill(grid.AmpArrive, float.MaxValue);
			grid.AmpDir = new Vector2[grid.CellCount];
		}
		var sink = new RaySink { Grid = grid };
		bool any = fan ? BuildFanField(frontOrigin, dir, halfAngleDeg, reflectWalls, ref sink) : BuildOmniField(frontOrigin, ref sink);
		if (!any) {
			return;
		}
		// 回声距离场（仅全向波）：前缘是从 MaxRadius 向内回缩的**完整圆环**（到达时刻只看欧氏半径），
		// Blocked 模式下被地形挡住的地方不回应——圆环在那里断开
		if (!fan) {
			BuildEchoField(frontOrigin, grid);
		}
		// 每帧遍历范围：扇形波只需射线走过的包围盒；全向波的回声是完整圆环（可能扫到发声段没走到的单元），用整个窗口
		grid.FinishIterRange(!fan);
		int covered = CountCovered(grid);
		var wave = new WaveData {
			Start = _simTime,
			Origin = origin,
			Speed = WaveSpeed,
			Radius = MaxRadius,
			Delay = EchoDelay,
			Intensity = Mathf.Clamp(intensity, 0.05f, 1f),
			// 反射段可超出 MaxRadius（反射后如新波继续传播）：生命周期按最远到达路径延长
			TOut = Mathf.Max(MaxRadius, sink.MaxPath) / WaveSpeed,
			HasEcho = !fan,
			Grid = grid
		};
		_waves.Add(wave);
		Log.Debug($"声波发出 origin={origin.Round()} 强度={wave.Intensity:F2} 扇形={fan} 回声={wave.HasEcho} 回声模式={EchoMode} " +
			$"覆盖={covered}格 窗口={grid.Cols}×{grid.Rows}({grid.CellCount}格) 实际范围=({grid.MinCol},{grid.MinRow})-({grid.MaxCol},{grid.MaxRow})");
		if (Input.GetConnectedJoypads().Count > 0) {
			// 发声：一次强震后立即停止（振幅按设置中的震动强度缩放）
			Input.StartJoyVibration(0, 0f, 1f * Settings.VibrationStrength, 0.15f);
		}
	}

	/// <summary>
	/// 建立某道声波的局部网格窗口（射线从 center 出发，= 声源所在单元中心）：
	/// - 全向波 / 无反射墙的扇形波：射线被 MaxRadius 封顶，窗口 = Bounds ∩ (center ± (MaxRadius + 2 格))；
	/// - 有反射墙的扇形波：反射段可能折返到射程之外（每段预算重新充满），窗口保守地取整张全局网格；
	/// - 窗口一定包含声源所在单元（射程方框夹进网格并夹到至少含声源那一格），
	///   保证射线起点、回声圆环都在窗口内。
	/// </summary>
	private LocalGrid CreateLocalGrid(Vector2 center, bool fan, bool reflectWalls) {
		int col0, col1, row0, row1;
		if (fan && reflectWalls) {
			col0 = 0;
			col1 = _cols - 1;
			row0 = 0;
			row1 = _rows - 1;
		} else {
			float reach = MaxRadius + CellSize * 2f;
			Vector2 rel = center - Bounds.Position;
			int srcCol = Mathf.Clamp(Mathf.FloorToInt(rel.X / CellSize), 0, _cols - 1);
			int srcRow = Mathf.Clamp(Mathf.FloorToInt(rel.Y / CellSize), 0, _rows - 1);
			col0 = Mathf.Clamp(Mathf.FloorToInt((rel.X - reach) / CellSize), 0, srcCol);
			col1 = Mathf.Clamp(Mathf.CeilToInt((rel.X + reach) / CellSize), srcCol, _cols - 1);
			row0 = Mathf.Clamp(Mathf.FloorToInt((rel.Y - reach) / CellSize), 0, srcRow);
			row1 = Mathf.Clamp(Mathf.CeilToInt((rel.Y + reach) / CellSize), srcRow, _rows - 1);
		}
		var grid = new LocalGrid {
			Col0 = col0, Row0 = row0,
			Cols = col1 - col0 + 1, Rows = row1 - row0 + 1
		};
		grid.CellCount = grid.Cols * grid.Rows;
		return grid;
	}

	/// <summary>窗口内被声波覆盖（首次到达可达）的单元数，供日志诊断。</summary>
	private static int CountCovered(LocalGrid grid) {
		if (!grid.HasIter) {
			return 0;
		}
		int covered = 0;
		for (int row = grid.IterRow0; row <= grid.IterRow1; row++) {
			int baseIdx = (row - grid.Row0) * grid.Cols - grid.Col0;
			for (int col = grid.IterCol0; col <= grid.IterCol1; col++) {
				if (grid.Dist[baseIdx + col] >= 0f) {
					covered++;
				}
			}
		}
		return covered;
	}

	/// <summary>
	/// 全向声波：360° 密集射线（0.25° 间距），遇实体即停——与扇形波一致：
	/// 被墙阻挡后不绕行，穿过缝隙后保持缝隙形状，不会在障碍后重新扩散。
	/// </summary>
	private bool BuildOmniField(Vector2 start, ref RaySink sink) {
		int rayCount = Mathf.Max(64, Mathf.CeilToInt(360f / OmniRayDeg));
		float cap = MaxRadius + CellSize * 0.5f;
		for (int i = 0; i < rayCount; i++) {
			float ang = Mathf.Tau * i / rayCount;
			CastRay(start, new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)), cap, 1f, 0f, false, out _, out _, ref sink);
		}
		return true;
	}

	/// <summary>
	/// 定向扇形声波：沿扇形发射密集射线，遇实体即停——被墙阻挡后不会在墙后重新扩散。
	/// 射线间距约 0.4°，保证最远处相邻射线间隔小于一个单元。
	/// 若关卡中存在反射墙（组 "reflect_wall"），扇形射线按镜面法则反射（入射角=反射角）：
	/// 全向声波不反射（遇到反射墙同普通地形止步），仅扇形声波反射；
	/// 反射射线沿“入射段 + 反射段”累计距离，波前经过反射后的区域照常点亮/触发。
	/// 射线穿过声波放大器（组 "wave_amplifier"）时每进入一个放大区乘一次倍率，
	/// 反射段之间倍率跨段累计（每条射线路径独立累计）。
	/// </summary>
	private bool BuildFanField(Vector2 start, Vector2 dir, float halfAngleDeg, bool reflect, ref RaySink sink) {
		int rayCount = Mathf.Max(2, Mathf.CeilToInt(halfAngleDeg * 2f / 0.4f));
		float cap = MaxRadius + CellSize * 0.5f;
		Vector2 baseDir = dir.Normalized();
		var space = reflect ? GetWorld2D().DirectSpaceState : null;
		for (int i = 0; i <= rayCount; i++) {
			float ang = Mathf.DegToRad(Mathf.Lerp(-halfAngleDeg, halfAngleDeg, i / (float)rayCount));
			Vector2 rd = baseDir.Rotated(ang);
			if (reflect) {
				CastFanRayWithReflect(start, rd, cap, ref sink, space);
			} else {
				CastRay(start, rd, cap, 1f, 0f, false, out _, out _, ref sink);
			}
		}
		return true;
	}

	/// <summary>
	/// 扇形声波的镜面反射射线：先做物理射线检测定位墙面（层 SolidMask），
	/// 命中反射墙则在命中点按法线反射后继续（最多 MaxFanReflections 次），
	/// 命中普通地形/门则止步；两段之间用网格步进 CastRay 累计距离场，
	/// 反射墙自身仍按实心单元处理（网格步进遇到实心即停，与全向波一致）。
	/// 反射段的距离按“从声源起的全程路径”计（traveled 跨段累计），因此波前按到达时刻折返，
	/// 不会提前画出反射波；每反射一次单段预算重新充满（反射波如新波从墙面继续传播，回程可满程）。
	/// 倍率（放大器放大结果）沿反射段跨段累计：反射后的线段继承反射前的倍率。
	/// </summary>
	private void CastFanRayWithReflect(Vector2 start, Vector2 rdIn, float cap, ref RaySink sink, PhysicsDirectSpaceState2D space) {
		Vector2 rd = rdIn.Normalized();
		Vector2 pos = start;
		float mul = 1f;
		// 累计路径长度（px，从声源起算）：反射段之间的“到达时刻”按总路径计
		float totalTravel = 0f;
		int bounces = 0;
		var ray = new PhysicsRayQueryParameters2D {
			CollisionMask = SolidMask,
			CollideWithBodies = true,
			CollideWithAreas = false
		};
		while (true) {
			ray.From = pos;
			ray.To = pos + rd * cap; // 单段预算（MaxRadius+半单元），反射后重新充满
			var hit = space.IntersectRay(ray);
			if (hit.Count == 0) {
				// 无墙：整段直行到最大半径
				CastRay(pos, rd, totalTravel + cap + CellSize * 0.5f, mul, totalTravel, bounces > 0, out _, out _, ref sink);
				return;
			}
			var hitPos = (Vector2)hit["position"];
			float seg = pos.DistanceTo(hitPos);
			// 走到墙面为止（网格实心单元会自然止步，多给半单元余量防网格/物理面差一格）
			CastRay(pos, rd, totalTravel + cap + CellSize * 0.5f, mul, totalTravel, bounces > 0, out mul, out totalTravel, ref sink);
			var collider = hit["collider"].As<GodotObject>();
			if (collider is not Node2D node || !node.IsInGroup("reflect_wall")) {
				return; // 普通地形/关着的门：阻挡声波
			}
			Vector2 nrm = ((Vector2)hit["normal"]).Normalized();
			rd = (rd - 2f * rd.Dot(nrm) * nrm).Normalized();
			pos = hitPos + rd * CellSize; // 离开墙面，避免下段射线再次命中同一面
			bounces++;
			if (bounces > MaxFanReflections) {
				// 反射次数预算耗尽：不再反弹，沿当前方向直行衰减
				CastRay(pos, rd, totalTravel + cap + CellSize * 0.5f, mul, totalTravel, true, out _, out _, ref sink);
				return;
			}
		}
	}

	/// <summary>
	/// 网格步进射线：半单元步长推进，标记沿途单元距离，遇实体或出界停止。
	/// 倍率：射线从 baseMul 起，每进入一个放大器单元（_ampId 变化）乘一次倍率；
	/// 放大倍率与首次到达距离解耦地写入 sink（反射后到达的放大段也能被强声波推动查询到）。
	/// baseTravel 为反射段之前的累计路径长度，cap 为本次线段从声源起的全局预算：
	/// traveled 从声源起算——首次到达写入 Dist；反射段（reflected=true，已发生过反弹的射线）
	/// 对首次到达之后的再次扫过写入 ReflDist（同一波前的其他角度射线交叉同格不误记）——
	/// 反射波前据此按“到达时刻”绘制，不会在声波到达墙面前提前出现。
	/// 写入目标是本道声波的局部窗口（sink.Grid）：窗口外不写（窗口按射程分配，正常不会发生）。
	/// </summary>
	private void CastRay(Vector2 start, Vector2 rd, float cap, float baseMul, float baseTravel, bool reflected, out float endMul, out float endTravel, ref RaySink sink) {
		Vector2 pos = start;
		float traveled = baseTravel;
		float mul = baseMul;
		int lastAmp = -1;
		float stepLen = CellSize * 0.5f;
		bool hasAmp = _ampId != null;
		bool inside = WorldToCell(pos, out int col, out int row);
		while (traveled <= cap) {
			if (traveled > sink.MaxPath) {
				sink.MaxPath = traveled;
			}
			if (inside) {
				int idx = row * _cols + col;
				if (hasAmp) {
					// 进入新的放大区：本射线后续路径按倍率放大（每次穿过放大一次）
					int ampId = _ampId[idx];
					if (ampId != lastAmp) {
						if (ampId >= 0) {
							mul *= _ampFactors[ampId];
						}
						lastAmp = ampId;
					}
				}
				MarkRayCell(ref sink, col, row, traveled, mul, rd, reflected);
			}
			pos += rd * stepLen;
			traveled += stepLen;
			inside = WorldToCell(pos, out int nc, out int nr);
			if (!inside) {
				break;
			}
			if ((nc != col || nr != row) && _solid[nr * _cols + nc]) {
				break;
			}
			col = nc;
			row = nr;
		}
		endMul = mul;
		endTravel = traveled;
	}

	/// <summary>
	/// 把一个"射线走过"的单元写进本道声波的局部窗口：距离场（首次到达 / 反射段再次扫过）、
	/// 最大放大倍率及其到达路径长度与方向，并标记实际走过范围（每帧只遍历这块包围盒）。
	/// 窗口外的单元直接忽略（窗口按射程窗口分配，正常不会写出窗口）。
	/// </summary>
	private static void MarkRayCell(ref RaySink sink, int col, int row, float traveled, float mul, Vector2 dir, bool reflected) {
		LocalGrid grid = sink.Grid;
		if (!grid.Contains(col, row)) {
			return;
		}
		int i = grid.Index(col, row);
		grid.Touch(col, row);
		if (grid.AmpMax != null && mul > grid.AmpMax[i]) {
			// 记录本单元最强的放大倍率与其到达路径长度/方向（供强声波推动查询）
			grid.AmpMax[i] = mul;
			grid.AmpArrive[i] = traveled;
			grid.AmpDir[i] = dir;
		}
		float d = grid.Dist[i];
		if (d < 0f || traveled < d) {
			grid.Dist[i] = traveled;
		} else if (reflected && grid.ReflDist != null && (grid.ReflDist[i] < 0f || traveled < grid.ReflDist[i])) {
			// 反射段：首次到达之后的再次扫过，记录最早的一次后续到达
			grid.ReflDist[i] = traveled;
		}
	}

	/// <summary>
	/// 构建回声距离场（写进本道声波的局部窗口）：单元值 = 回声前缘扫过该单元时的“剩余距离”
	/// （前缘是从 MaxRadius 递减到 0 的**完整圆环**，故值越大越早被扫到）。
	/// 到达时刻只看该单元到声源的**欧氏半径**——回声始终是一个真正的圆，与发声波的形状无关。
	/// - <see cref="EchoModel.Blocked"/>（默认）：圆环**被地形阻挡**——只有与声源有视线
	///   （发声段射线到得了，即 dist ≥ 0）的单元会被扫到；墙后/遮挡区记为 -1（永不回应），
	///   圆环在那里断开，回声也触发不到被挡住的机关。
	/// - <see cref="EchoModel.Through"/>（旧行为）：不看地形，完整圆环一路扫过，墙后照样亮。
	/// </summary>
	private void BuildEchoField(Vector2 origin, LocalGrid grid) {
		var echo = new float[grid.CellCount];
		Array.Fill(echo, -1f);
		float cap = MaxRadius + CellSize * 0.5f;
		bool blocked = EchoMode == EchoModel.Blocked;
		int reached = 0;
		for (int row = 0; row < grid.Rows; row++) {
			for (int col = 0; col < grid.Cols; col++) {
				int i = row * grid.Cols + col;
				if (blocked && grid.Dist[i] < 0f) {
					continue; // 与声源之间隔着地形：回声到不了这里
				}
				float eu = CellCenter(grid.Col0 + col, grid.Row0 + row).DistanceTo(origin);
				if (eu <= cap) {
					echo[i] = eu;
					reached++;
				}
			}
		}
		if (blocked) {
			Log.Debug($"回声场（完整圆环·受地形阻挡）可及={reached}/{grid.CellCount} 格");
		}
		grid.EchoDist = echo;
	}

	private int FindFreeNear(Vector2 pos) {
		int baseIdx = WorldToIndex(pos);
		if (baseIdx < 0) {
			return -1;
		}
		int col = baseIdx % _cols;
		int row = baseIdx / _cols;
		for (int dr = -2; dr <= 2; dr++) {
			for (int dc = -2; dc <= 2; dc++) {
				int nr = row + dr;
				int nc = col + dc;
				if (nr < 0 || nr >= _rows || nc < 0 || nc >= _cols) {
					continue;
				}
				int ni = nr * _cols + nc;
				if (!_solid[ni]) {
					return ni;
				}
			}
		}
		return -1;
	}

	public override void _PhysicsProcess(double delta) {
		_simTime += delta * 1000.0;
		// 预热：先跳过 1 个物理帧等地形碰撞体注册，再在物理帧里算实心网格；
		// 结果为空（地形还没注册好）就继续重试，查到地形立刻收工，重试用尽则报警告便于排查。
		if (_solidWarmupLeft > 0) {
			_solidWarmupLeft--;
			// 地形也可能是在别的节点 _Ready 里才生成的：等所有 _Ready 跑完再校准一次网格范围
			if (_solidWarmupLeft == 0 && FitBoundsToTerrain()) {
				RebuildGridMetrics();
			}
		} else if (_solidCells == 0 && _solidRetriesLeft > 0) {
			_solidRetriesLeft--;
			_solidDirty = true;
			EnsureSolid();
			if (_solidCells > 0) {
				_solidRetriesLeft = 0;
			} else if (_solidRetriesLeft == 0) {
				Log.Warn($"实心网格未查到任何地形（{_cellCount} 格全空），声波将不会被地形遮挡：" +
					$"请检查地形的物理层是否为 layer {SolidMask}（地形），以及 WaveField.Bounds 是否覆盖地形");
			}
		}
		if (_waves.Count == 0 && !BlindMode && !_hasGlow) {
			return;
		}
		bool joy = Input.GetConnectedJoypads().Count > 0;
		for (int i = _waves.Count - 1; i >= 0; i--) {
			var w = _waves[i];
			float t = (float)((_simTime - w.Start) / 1000.0);
			float echoStart = w.TOut + w.Delay;
			float end = w.HasEcho ? echoStart + w.TOut : w.TOut;
			if (t >= end) {
				_waves.RemoveAt(i);
				Log.Trace($"声波消散 origin={w.Origin.Round()} 历时={(t * 1000f):F0}ms");
				// 辉光层移交残余列表：波身结束后继续透明淡出（不弹硬切）；
				// 距离场等大数组立刻释放（只留辉光层与遍历范围）
				w.Grid.ReleaseHeavyFields();
				_fadingGlows.Add(w.Grid);
				continue;
			}
			int phase = t < w.TOut ? 1 : (w.HasEcho && t < echoStart ? 2 : 3);
			if (phase != w.Phase) {
				w.Phase = phase;
				if (phase == 3) {
					Log.Debug($"回声开始收敛 origin={w.Origin.Round()} 延迟={w.Delay}s");
					AudioManager.Instance?.PlaySe("echo", 0.5f);
					if (joy) {
						// 回声生效：短促、清脆的小震（按设置中的震动强度缩放）
						float s = Settings.VibrationStrength;
						Input.StartJoyVibration(0, 0.35f * s, 0.35f * s, 0.12f);
					}
				}
			}
		}
		if (_waves.Count > 0 && joy) {
			// 传播中（延迟期）：震动由弱渐强，回声生效前达到峰值
			var w = _waves[^1];
			float t = (float)((_simTime - w.Start) / 1000.0);
			if (w.Phase == 1 || w.Phase == 2) {
				float rampEnd = w.TOut + (w.HasEcho ? w.Delay : 0f);
				float amp = 0.12f + 0.5f * Mathf.Clamp(t / Mathf.Max(0.01f, rampEnd), 0f, 1f);
				float s = Settings.VibrationStrength;
				Input.StartJoyVibration(0, amp * 0.35f * s, amp * s, 0.05f);
			}
		}
		// 辉光：每道声波独立一层（透明叠加——后发的声波叠在先发之上，互不覆盖），
		// 仅波前附近的单元点亮（短尾迹）；回声辉光为收回前沿外侧的尾迹（跟随收缩方向），
		// 不再出现在波前前方；发声段截止后不再点亮——回声触发前范围内发光已全部消散。
		// 只遍历本道声波的遍历范围（扇形=射线走过的包围盒，全向=射程窗口），不扫整张网格。
		foreach (var w in _waves) {
			float t = (float)((_simTime - w.Start) / 1000.0);
			float echoStart = w.TOut + w.Delay;
			bool echo = w.HasEcho && t >= echoStart;
			float fill = 0.5f + 0.5f * w.Intensity;
			float win = Mathf.Max(24f, AfterglowDecay * w.Speed);
			LocalGrid g = w.Grid;
			if (!g.HasIter) {
				continue;
			}
			if (!echo && t < w.TOut) {
				float front = w.Speed * t;
				for (int row = g.IterRow0; row <= g.IterRow1; row++) {
					int baseIdx = (row - g.Row0) * g.Cols - g.Col0;
					for (int col = g.IterCol0; col <= g.IterCol1; col++) {
						int i = baseIdx + col;
						float d = g.Dist[i];
						if (d >= 0f && d <= front && front - d <= win && g.Glow[i] < fill) {
							g.Glow[i] = fill;
						}
					}
				}
				// 反射段折返再扫过：按到达时刻重新点亮
				if (g.ReflDist != null) {
					for (int row = g.IterRow0; row <= g.IterRow1; row++) {
						int baseIdx = (row - g.Row0) * g.Cols - g.Col0;
						for (int col = g.IterCol0; col <= g.IterCol1; col++) {
							int i = baseIdx + col;
							float rd = g.ReflDist[i];
							if (rd >= 0f && rd <= front && front - rd <= win && g.Glow[i] < fill) {
								g.Glow[i] = fill;
							}
						}
					}
				}
			} else if (echo) {
				float front = w.Radius - w.Speed * (t - echoStart);
				float echoWin = Mathf.Max(20f, win * 0.35f);
				for (int row = g.IterRow0; row <= g.IterRow1; row++) {
					int baseIdx = (row - g.Row0) * g.Cols - g.Col0;
					for (int col = g.IterCol0; col <= g.IterCol1; col++) {
						int i = baseIdx + col;
						float e = g.EchoDist == null ? -1f : g.EchoDist[i];
						// 尾迹在圆环外侧（回声已扫过的一侧，跟随收缩方向），不点亮波前前方的区域
						if (e >= 0f && e >= front && e - front <= echoWin && g.Glow[i] < fill) {
							g.Glow[i] = fill;
						}
					}
				}
			}
		}
		float decay = (float)delta / Mathf.Max(0.05f, AfterglowDecay);
		bool anyGlow = false;
		foreach (var w in _waves) {
			if (DecayGlow(w.Grid, decay)) {
				anyGlow = true;
			}
		}
		// 已结束声波的辉光残余继续淡出（波身消失后辉光不弹硬切）
		for (int i = _fadingGlows.Count - 1; i >= 0; i--) {
			if (DecayGlow(_fadingGlows[i], decay)) {
				anyGlow = true;
			} else {
				_fadingGlows.RemoveAt(i);
			}
		}
		_hasGlow = anyGlow;
		if (BlindMode && _boundary != null) {
			UpdateReveal((float)delta, _simTime);
		}
		QueueRedraw();
	}

	/// <summary>衰减一层辉光；返回是否仍有余辉（供 _hasGlow 判断是否继续重绘）。
	/// 只遍历该层自己的遍历范围（声波实际覆盖的包围盒），不扫整张网格。</summary>
	private static bool DecayGlow(LocalGrid grid, float decay) {
		if (grid.Glow == null || !grid.HasIter) {
			return false;
		}
		bool any = false;
		for (int row = grid.IterRow0; row <= grid.IterRow1; row++) {
			int baseIdx = (row - grid.Row0) * grid.Cols - grid.Col0;
			for (int col = grid.IterCol0; col <= grid.IterCol1; col++) {
				int i = baseIdx + col;
				if (grid.Glow[i] > 0f) {
					grid.Glow[i] = Mathf.Max(0f, grid.Glow[i] - decay);
					if (grid.Glow[i] > 0f) {
						any = true;
					}
				}
			}
		}
		return any;
	}

	/// <summary>盲视模式：声波覆盖到的边界单元轮廓点亮，随后按 RevealDuration 衰减。
	/// 点亮只遍历各道声波的遍历范围，衰减只遍历被点亮过的范围（不扫整张网格）。</summary>
	private void UpdateReveal(float dt, double now) {
		if (_reveal == null) {
			_reveal = new float[_cellCount];
		}
		float decay = dt / Mathf.Max(0.01f, RevealDuration);
		foreach (var w in _waves) {
			float t = (float)((now - w.Start) / 1000.0);
			float echoStart = w.TOut + w.Delay;
			bool echo = t >= echoStart;
			LocalGrid g = w.Grid;
			if (!g.HasIter) {
				continue;
			}
			for (int row = g.IterRow0; row <= g.IterRow1; row++) {
				int gridBase = row * _cols;
				for (int col = g.IterCol0; col <= g.IterCol1; col++) {
					int i = gridBase + col;
					if (_boundary[i] != 0 && _reveal[i] < 1f && IsCellCovered(w, col, row, t, echo, echoStart)) {
						_reveal[i] = 1f;
						TouchReveal(col, row);
					}
				}
			}
		}
		if (_revealCol1 >= _revealCol0) {
			for (int row = _revealRow0; row <= _revealRow1; row++) {
				int baseIdx = row * _cols - _revealCol0;
				for (int col = _revealCol0; col <= _revealCol1; col++) {
					int i = baseIdx + col;
					if (_reveal[i] > 0f) {
						_reveal[i] = Mathf.Max(0f, _reveal[i] - decay);
					}
				}
			}
		}
	}

	/// <summary>记录轮廓被点亮到的单元范围（衰减时只遍历这块）。</summary>
	private void TouchReveal(int col, int row) {
		if (col < _revealCol0) {
			_revealCol0 = col;
		}
		if (col > _revealCol1) {
			_revealCol1 = col;
		}
		if (row < _revealRow0) {
			_revealRow0 = row;
		}
		if (row > _revealRow1) {
			_revealRow1 = row;
		}
	}

	/// <summary>
	/// 查询波前是否经过某位置：每个（声波 × 阶段 × 元素）只上报一次，
	/// 结果追加到 events 中。元素按位置取样，可支持移动中的物体。
	/// 强度按该单元记录的最大放大倍率上报（放大器放大后的声波；回声段无放大）。
	/// </summary>
	public void QueryPass(Vector2 worldPos, ulong elementId, List<WavePassEvent> events) {
		if (_waves.Count == 0) {
			return;
		}
		if (!WorldToCell(worldPos, out int col, out int row)) {
			return;
		}
		double now = _simTime;
		foreach (var w in _waves) {
			LocalGrid g = w.Grid;
			if (!g.Contains(col, row)) {
				continue; // 不在本道声波的射程窗口内：没被覆盖
			}
			int idx = g.Index(col, row);
			float d = g.Dist[idx];
			if (d < 0f) {
				continue;
			}
			float t = (float)((now - w.Start) / 1000.0);
			if (!w.Delivered.TryGetValue(elementId, out byte flags)) {
				flags = 0;
			}
			float echoStart = w.TOut + w.Delay;
			float echoD = g.EchoDist == null ? -1f : g.EchoDist[idx];
			if ((flags & 1) == 0 && t >= d / w.Speed) {
				flags |= 1;
				Vector2 dir = g.AmpMax != null && g.AmpDir[idx].LengthSquared() > 0.01f
					? g.AmpDir[idx] : (w.Origin - worldPos).Normalized();
				events.Add(new WavePassEvent {
					Phase = WavePhase.Outbound,
					Intensity = w.Intensity * AmpAt(g, idx), Origin = w.Origin,
					Dir = dir,
					SimTimeMs = (float)(w.Start + d / w.Speed * 1000.0)
				});
			}
			if (w.HasEcho && (flags & 2) == 0 && echoD >= 0f && t >= echoStart + (w.Radius - echoD) / w.Speed) {
				flags |= 2;
				events.Add(new WavePassEvent {
					Phase = WavePhase.Echo, Intensity = w.Intensity * EchoIntensityFactor, Origin = w.Origin,
					Dir = (w.Origin - worldPos).Normalized(),
					SimTimeMs = (float)(w.Start + (echoStart + (w.Radius - echoD) / w.Speed) * 1000.0)
				});
			}
			w.Delivered[elementId] = flags;
		}
	}

	/// <summary>某声波在指定单元的放大强度倍率（无放大器为 1）。</summary>
	private static float AmpAt(LocalGrid grid, int idx) {
		return grid.AmpMax != null ? grid.AmpMax[idx] : 1f;
	}

	/// <summary>
	/// 查询玩家当前位置是否被“强声波”覆盖（放大后强度 &gt; 1.0 的声波可推动玩家）：
	/// 返回该单元记录的最大放大倍率段（倍率/到达路径长度/传播方向满足时上报一次）。
	/// 每道声波对同一元素只上报一次（返回值已去重）；强度与方向分别通过 out 参数返回。
	/// </summary>
	public bool QueryPushAt(Vector2 worldPos, ulong elementId, out float intensity, out Vector2 dir) {
		intensity = 0f;
		dir = Vector2.Zero;
		if (_waves.Count == 0) {
			return false;
		}
		if (!WorldToCell(worldPos, out int col, out int row)) {
			return false;
		}
		double now = _simTime;
		bool pushed = false;
		foreach (var w in _waves) {
			LocalGrid g = w.Grid;
			if (g.AmpMax == null || !g.Contains(col, row)) {
				continue;
			}
			int idx = g.Index(col, row);
			float t = (float)((now - w.Start) / 1000.0);
			float m = g.AmpMax[idx];
			if (m <= 1f) {
				continue;
			}
			float arrive = g.AmpArrive[idx];
			if (arrive >= float.MaxValue || t * w.Speed < arrive) {
				continue; // 放大段尚未到达该单元
			}
			if (!w.Delivered.TryGetValue(elementId, out byte flags)) {
				flags = 0;
			}
			if ((flags & 4) != 0) {
				continue; // 这道声波已推过该元素
			}
			float strength = w.Intensity * m;
			if (strength <= 1f) {
				continue; // 只有强度超过玩家最大强度（1.0）的声波才推动玩家
			}
			flags |= 4;
			w.Delivered[elementId] = flags;
			if (strength > intensity) {
				intensity = strength;
				dir = g.AmpDir[idx];
			}
			pushed = true;
		}
		return pushed;
	}

	public override void _Draw() {
		double now = _simTime;
		if (DrawWash && _hasGlow) {
			DrawGlowField();
		}
		if (_waves.Count == 0) {
			return;
		}
		foreach (var w in _waves) {
			float t = (float)((now - w.Start) / 1000.0);
			float echoStart = w.TOut + w.Delay;
			bool echo = w.HasEcho && t >= echoStart;
			DrawWave(w, t, echo, echoStart);
		}
	}

	/// <summary>
	/// 范围辉光：每道声波独立一层（透明混合，重叠处自然叠加变亮），
	/// 按波次顺序绘制——后发的声波叠在先发之上；已结束声波的残余辉光垫在最底层继续淡出。
	/// </summary>
	private void DrawGlowField() {
		foreach (var g in _fadingGlows) {
			DrawGlowLayer(g);
		}
		foreach (var w in _waves) {
			DrawGlowLayer(w.Grid);
		}
	}

	/// <summary>绘制单层辉光：按行合并同亮度段，减少 draw call（单元大小恒定，alpha 按 GlowLevels 分档量化）。
	/// 亮度档向下降档跳变（随时间/距离的衰减呈台阶状），呈现像素化渐变。</summary>
	private void DrawGlowLayer(LocalGrid grid) {
		float[] glow = grid.Glow;
		if (glow == null || !grid.HasIter) {
			return;
		}
		int levels = Mathf.Max(2, GlowLevels);
		for (int row = grid.IterRow0; row <= grid.IterRow1; row++) {
			int baseIdx = (row - grid.Row0) * grid.Cols - grid.Col0;
			int runStart = -1;
			byte runLv = 0;
			for (int col = grid.IterCol0; col <= grid.IterCol1; col++) {
				float gl = glow[baseIdx + col];
				byte lv = gl > 0f ? (byte)Mathf.Min(levels, (int)(gl * levels + 0.5f)) : (byte)0;
				if (lv == 0) {
					if (runStart >= 0) {
						FlushLevelRun(row, runStart, col, runLv, WashColor, WashColor.A, levels);
						runStart = -1;
					}
					continue;
				}
				if (runStart < 0) {
					runStart = col;
					runLv = lv;
				} else if (lv != runLv) {
					FlushLevelRun(row, runStart, col, runLv, WashColor, WashColor.A, levels);
					runStart = col;
					runLv = lv;
				}
			}
			if (runStart >= 0) {
				FlushLevelRun(row, runStart, grid.IterCol1 + 1, runLv, WashColor, WashColor.A, levels);
			}
		}
	}

	/// <summary>按行合并同亮度段绘制，减少 draw call（单元大小恒定，alpha 分段量化）。</summary>
	private void FlushLevelRun(int row, int colStart, int colEnd, byte level, Color rgb, float baseA, int levels) {
		if (level <= 0 || colEnd <= colStart) {
			return;
		}
		var c = rgb;
		c.A = baseA * (level / (float)levels);
		DrawRect(new Rect2(
			Bounds.Position.X + colStart * CellSize,
			Bounds.Position.Y + row * CellSize,
			(colEnd - colStart) * CellSize,
			CellSize), c);
	}

	private void DrawWave(WaveData w, float t, bool echo, float echoStart) {
		bool delay = !echo && t >= w.TOut; // 延迟期只有辉光，没有行进中的波前
		if (!delay) {
			// 回声：完整圆环从 Radius(=MaxRadius) 回缩到 0（到达时刻只看欧氏半径；
			// Blocked 模式下被地形挡住的地方没有值，圆环在那里断开）
			float front = echo ? w.Radius - w.Speed * (t - echoStart) : w.Speed * t;
			DrawRing(w, front, echo);
		}
		// 原点闪光（发声瞬间）
		if (!echo && t < 0.35f) {
			float k = 1f - t / 0.35f;
			float ia = 0.5f + 0.5f * w.Intensity;
			var c = OutboundColor;
			c.A = 0.45f * k * ia;
			DrawCircle(w.Origin, 2.5f + 3.5f * (1f - k), c);
			var c2 = OutboundColor;
			c2.A = 0.14f * k * ia;
			DrawCircle(w.Origin, 7f + 24f * (1f - k), c2);
		}
	}

	/// <summary>
	/// 波前亮环（像素风）：单格核心亮环 + 弱外晕。
	/// 发声段：射线距离场（被墙阻挡即停、不绕墙）+ 反射段距离场（按折返后的到达时刻再扫过，
	/// 即“声波到达墙面后才反射”，不会提前画出反射波）；
	/// 回声段：完整圆环从 MaxRadius 回缩到 0（场值=欧氏半径，故永远是圆）；
	/// Blocked 模式下被地形挡住的单元没有场值，圆环在那里断开。
	/// </summary>
	private void DrawRing(WaveData w, float front, bool echo) {
		LocalGrid grid = w.Grid;
		float[] field = echo ? grid.EchoDist : grid.Dist;
		if (field == null) {
			return;
		}
		float bCore = CellSize * 0.85f;
		float bHalo = CellSize * 2.2f;
		var baseColor = echo ? EchoColor : OutboundColor;
		// 调暗描边：核心亮环总亮度 ×0.65，外晕按比例随之变暗
		float ringA = baseColor.A * (0.35f + 0.65f * w.Intensity) * 0.65f;
		int coreLv = WashLevels;
		int haloLv = (byte)(WashLevels * 0.32f + 0.5f);
		DrawRingPass(grid, field, front, baseColor, ringA, coreLv, haloLv, bCore, bHalo);
		if (!echo && grid.ReflDist != null) {
			// 反射段：到达墙面折返后的第二次扫过（亮度略低以示区分）
			DrawRingPass(grid, grid.ReflDist, front, baseColor, ringA * 0.8f, coreLv, haloLv, bCore, bHalo);
		}
	}

	/// <summary>按行 RLE 合并绘制单圈亮环（field 内的距离值即波前到达时刻，front 为当前波前）。
	/// 只遍历该声波的遍历范围（扇形=射线走过的包围盒，全向=射程窗口），不扫整张网格。</summary>
	private void DrawRingPass(LocalGrid grid, float[] field, float front, Color baseColor, float ringA, int coreLv, int haloLv, float bCore, float bHalo) {
		if (!grid.HasIter) {
			return;
		}
		for (int row = grid.IterRow0; row <= grid.IterRow1; row++) {
			int baseIdx = (row - grid.Row0) * grid.Cols - grid.Col0;
			int runStart = -1;
			byte runLv = 0;
			for (int col = grid.IterCol0; col <= grid.IterCol1; col++) {
				float d = field[baseIdx + col];
				byte lv = 0;
				if (d >= 0f) {
					float b = Mathf.Abs(d - front);
					if (b <= bCore) {
						lv = (byte)coreLv;
					} else if (b <= bHalo) {
						lv = (byte)haloLv;
					}
				}
				if (lv == 0) {
					if (runStart >= 0) {
						FlushLevelRun(row, runStart, col, runLv, baseColor, ringA, WashLevels);
						runStart = -1;
					}
					continue;
				}
				if (runStart < 0) {
					runStart = col;
					runLv = lv;
				} else if (lv != runLv) {
					FlushLevelRun(row, runStart, col, runLv, baseColor, ringA, WashLevels);
					runStart = col;
					runLv = lv;
				}
			}
			if (runStart >= 0) {
				FlushLevelRun(row, runStart, grid.IterCol1 + 1, runLv, baseColor, ringA, WashLevels);
			}
		}
	}

	/// <summary>查询某世界坐标所在单元是否为实心（供关卡布置校验）。</summary>
	public bool IsSolidAt(Vector2 pos) {
		int idx = WorldToIndex(pos);
		return idx >= 0 && _solid != null && _solid[idx];
	}

	/// <summary>某单元当前是否被声波覆盖（发声扩散中 / 回声收拢中）。</summary>
	private static bool IsCellCovered(WaveData w, int col, int row, float t, bool echo, float echoStart) {
		LocalGrid grid = w.Grid;
		if (!grid.Contains(col, row)) {
			return false;
		}
		int idx = grid.Index(col, row);
		float d = grid.Dist[idx];
		if (d < 0f) {
			return false;
		}
		if (t < w.TOut) {
			return d <= w.Speed * t;
		}
		if (!echo) {
			return true;
		}
		float e = grid.EchoDist == null ? -1f : grid.EchoDist[idx];
		return e >= 0f && e >= w.Radius - w.Speed * (t - echoStart);
	}

	/// <summary>世界坐标 → 全局单元列/行；出界返回 false（出界处不写场、不算覆盖）。</summary>
	private bool WorldToCell(Vector2 pos, out int col, out int row) {
		Vector2 rel = pos - Bounds.Position;
		if (rel.X < 0f || rel.Y < 0f || rel.X >= Bounds.Size.X || rel.Y >= Bounds.Size.Y) {
			col = 0;
			row = 0;
			return false;
		}
		col = Mathf.Min((int)(rel.X / CellSize), _cols - 1);
		row = Mathf.Min((int)(rel.Y / CellSize), _rows - 1);
		return true;
	}

	private int WorldToIndex(Vector2 pos) {
		return WorldToCell(pos, out int col, out int row) ? row * _cols + col : -1;
	}

	private Vector2 CellCenter(int col, int row) {
		return Bounds.Position + new Vector2((col + 0.5f) * CellSize, (row + 0.5f) * CellSize);
	}
}
