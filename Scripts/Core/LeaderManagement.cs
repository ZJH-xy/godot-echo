using Godot;

public partial class LeaderManagement : Node2D {
	[Export] private TileMapLayer _tilemap = null;
	[Export] private Camera2D _camera = null;

	public override void _Ready() {
		_tilemap ??= GetNode<TileMapLayer>("TileMapLayer");
		var used = _tilemap.GetUsedRect().Grow(-1);
		var tileSize = _tilemap.TileSet.TileSize;

		_camera.LimitTop = used.Position.Y * tileSize.Y;
		_camera.LimitRight = used.End.X * tileSize.X;
		_camera.LimitBottom = used.End.Y * tileSize.Y;
		_camera.LimitLeft = used.Position.X * tileSize.X;
		_camera.ResetSmoothing();
	}
}
