shader_type canvas_item;
render_mode blend_mix, unshaded;

// 《回声》主菜单：鼠标点击处释放的“全向声波”冲击波。
// 与游戏内的声场同一套语言——从点击点向外扩散的一道圆形波前：
//   · 波前把**已画好的背景**按径向推开（折射扭曲，最近邻采样，像素拉伸不糊）；
//   · 波前外侧跟一圈很淡的反向推力（像被挤出去的回波），中间是干净的暗区（不整屏发白）；
//   · 波前最亮处描一道青白亮环；随后整体按 envelope 指数衰减，余波自己淡掉。
// 节点本身不画颜色（COLOR 只输出扭曲后的背景 + 亮环），所以没有声波经过的地方
// 与背景逐像素一致——背景上是纯色块也不会出现色带。
// 波参数（最多 MaxWaves 道，可叠加）由 Scripts/UI/Main.cs 逐帧写入 uniform。
// 注意：BackBufferCopy（Main.cs 也会在背景与节点之间插入一个）负责把背景拷进屏幕纹理，
// 本节点要摆在背景之上、UI 之下。

// 最多同时存在的冲击波道数（与 Main.cs 的常量一致）
const int MaxWaves = 8;

// 视口逻辑尺寸（px，640×360）：把屏幕 UV 换算成像素距离
uniform vec2 screen_size = vec2(640.0, 360.0);
// 像素块边长（px）：波前与扭曲按块采样，保留像素风（1 = 关闭像素化）
uniform float pixel_cell : hint_range(1.0, 16.0) = 6.0;
// 每道波：xy = 中心（屏幕 UV）、z = 当前扩张半径（px）、w = 强度包络（1→0）
uniform vec4 ripples[MaxWaves];
// 波前相对半径宽度（px）：|距离−半径| ≤ wave_width 处是亮环核心
uniform float wave_width : hint_range(0.5, 32.0) = 4.0;
// 波前折射位移（px）：正值把背景沿径向向外推开，做出“被声波挤开”的镜头感
uniform float refract_px : hint_range(0.0, 32.0) = 6.0;
// 波前亮环颜色（近白偏青，与游戏内发声波同色系）
uniform vec4 rim_color : source_color = vec4(0.75, 0.92, 1.0, 1.0);
// 亮环叠加强度上限（加色，背景暗时更像光）
uniform float rim_strength : hint_range(0.0, 1.0) = 0.75;
uniform sampler2D screen_tex : hint_screen_texture, filter_nearest;

void fragment() {
	// 像素块中心：波前按块跳变，波沿像素格推进
	vec2 px = floor(UV * screen_size / pixel_cell) * pixel_cell + pixel_cell * 0.5;
	vec2 hit = UV;

	// 叠加上限：8 道波的峰值不太可能同时落在同一像素，8×0.5 留足余量
	float rim = 0.0;

	for (int i = 0; i < MaxWaves; i++) {
		vec4 w = ripples[i];
		float strength = w.w;
		float radius = w.z;
		if (strength <= 0.002 || radius <= 0.0) {
			continue;
		}
		vec2 center = w.xy * screen_size;
		vec2 d = px - center;
		float r = length(d);
		if (r < 0.001) {
			continue;
		}
		vec2 dir = d / r;
		// 波前剖面：1 = 正好在波前上，向两侧迅速落到 0
		float front = 1.0 - smoothstep(0.0, wave_width, abs(r - radius));
		// 外侧拖尾：波前之后略宽一圈的柔和推力
		float tail = 1.0 - smoothstep(0.0, wave_width * 4.0, r - radius);
		float band = max(front, tail * 0.35);
		// 越靠近屏幕边缘折射越弱（避免采样越界把边缘像素拉成糊条）
		float edge = smoothstep(0.0, 24.0, min(min(px.x, screen_size.x - px.x), min(px.y, screen_size.y - px.y)));
		// 折射：沿径向把背景推开，波前最远处位移最大
		float push = (front - tail * 0.35) * refract_px * strength * edge;
		hit += dir * push / screen_size;
		rim = max(rim, front * strength * edge);
	}

	vec3 col = texture(screen_tex, clamp(hit, vec2(0.0), vec2(1.0))).rgb;
	col += rim_color.rgb * rim * rim_strength;
	COLOR = vec4(col, 1.0);
}
