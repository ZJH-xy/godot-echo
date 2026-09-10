using Godot;

/// <summary>
/// 盲视关卡轮廓层：在浓雾上方绘制声波勾勒出的地形轮廓线。
/// 读取 WaveField 的边界掩码与轮廓亮度，按单元逐段画线，随时间淡出。
/// </summary>
public partial class BlindReveal : Node2D {
	[Export] public Color OutlineColor = new Color(0.75f, 0.95f, 1f);
	[Export] public float LineWidth = 2f;

	private WaveField _field;

	public override void _Ready() {
		_field = GetTree().GetFirstNodeInGroup("wave_field") as WaveField;
	}

	public override void _Process(double delta) {
		QueueRedraw();
	}

	public override void _Draw() {
		if (_field == null || !_field.BlindMode) {
			return;
		}
		int cols = _field.GridCols;
		int rows = _field.GridRows;
		float cs = _field.GridCellSize;
		Vector2 origin = _field.GridBounds.Position;
		for (int row = 0; row < rows; row++) {
			for (int col = 0; col < cols; col++) {
				int idx = row * cols + col;
				byte b = _field.BoundaryAt(idx);
				if (b == 0) {
					continue;
				}
				float r = _field.RevealAt(idx);
				if (r <= 0.003f) {
					continue;
				}
				var color = OutlineColor;
				color.A *= r;
				float x = origin.X + col * cs;
				float y = origin.Y + row * cs;
				if ((b & 1) != 0) {
					DrawLine(new Vector2(x, y), new Vector2(x, y + cs), color, LineWidth);
				}
				if ((b & 2) != 0) {
					DrawLine(new Vector2(x + cs, y), new Vector2(x + cs, y + cs), color, LineWidth);
				}
				if ((b & 4) != 0) {
					DrawLine(new Vector2(x, y), new Vector2(x + cs, y), color, LineWidth);
				}
				if ((b & 8) != 0) {
					DrawLine(new Vector2(x, y + cs), new Vector2(x + cs, y + cs), color, LineWidth);
				}
			}
		}
	}
}
