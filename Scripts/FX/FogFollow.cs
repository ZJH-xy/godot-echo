using Godot;

/// <summary>
/// 浓雾层：跟随相机保持覆盖整个屏幕（盲视关卡）。
/// 世界空间中 z_index 高于普通场景元素，用于遮挡地形与物件。
/// </summary>
public partial class FogFollow : ColorRect {
	[Export] public Vector2 HalfSize = new Vector2(320, 180);

	public override void _Ready() {
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta) {
		var cam = GetViewport().GetCamera2D();
		if (cam != null) {
			GlobalPosition = cam.GetScreenCenterPosition() - HalfSize;
		}
	}
}
