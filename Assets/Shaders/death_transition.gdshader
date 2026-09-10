shader_type canvas_item;
render_mode blend_mix, unshaded;

// 《回声》死亡过渡（蔚蓝式）：死亡瞬间柔和脉冲 + 亮幕从死亡点向外像素化铺开，
// 满屏一瞬把玩家送回复活点，再从复活点向外收回——快速、清爽、不刺眼。
// 亮度只留在波前一线（rim_color），身后迅速沉为柔和青蓝（fill_color），避免全屏死白。
// cover / reveal / flash 由 Scripts/FX/DeathTransition.cs 按真实时间曲线驱动。

// 死亡点（屏幕 UV，亮幕铺开中心）
uniform vec2 cover_center = vec2(0.5, 0.5);
// 复活点（屏幕 UV，亮幕收回中心）
uniform vec2 reveal_center = vec2(0.5, 0.5);
// 视口尺寸（px）：像素块与距离都按它换算
uniform vec2 screen_size = vec2(640.0, 360.0);
// 铺开进度（0=无，1=铺满全屏）
uniform float cover : hint_range(0.0, 1.0) = 0.0;
// 收回进度（0=无，1=全部收回）
uniform float reveal : hint_range(0.0, 1.0) = 0.0;
// 死亡脉冲包络（1→0 快速衰减；实际强度再乘 flash_strength）
uniform float flash : hint_range(0.0, 1.0) = 0.0;
// 像素块边长（px）：波前按块推进，保留像素风
uniform float cell = 8.0;
// 波前亮边宽度（占屏幕对角线比例）
uniform float rim_width : hint_range(0.0, 0.3) = 0.05;
// 波前到亮幕主体的过渡长度（占屏幕对角线比例）：越短亮边越细，整屏越沉
uniform float gradient_len : hint_range(0.02, 1.0) = 0.16;
// 白闪强度（死亡瞬间的柔和脉冲，压低避免“闪光弹”）
uniform float flash_strength : hint_range(0.0, 1.0) = 0.3;
// 波前折射强度（0=关闭）：亮边处轻微扭曲屏幕，做出“声波扫过”的清透感
uniform float refract_strength : hint_range(0.0, 0.05) = 0.010;
// 亮幕主体颜色（柔和青蓝：铺开后整屏的底色，不刺眼）
uniform vec4 fill_color : source_color = vec4(0.44, 0.72, 0.90, 1.0);
// 波前亮边颜色（近白偏青：亮度只留在波前一线）
uniform vec4 rim_color : source_color = vec4(0.90, 0.98, 1.0, 1.0);
uniform sampler2D screen_tex : hint_screen_texture, filter_nearest;

void fragment() {
	// 取像素块中心：波前按块跳变，呈现方块推进的像素感
	vec2 px = floor(UV * screen_size / cell) * cell + cell * 0.5;
	float diag = length(screen_size);

	float dc = distance(px, cover_center * screen_size) / diag;
	float dr = distance(px, reveal_center * screen_size) / diag;

	// 已铺开 / 已收回：0 时整块不生效，避免“中心一格提前透明”的瑕疵
	float covered = cover > 0.0 ? step(dc, cover) : 0.0;
	float revealed = reveal > 0.0 ? step(dr, reveal) : 0.0;

	// 亮幕不透明度 = 铺开覆盖 − 收回挖空，并与柔和白闪取大
	float fa = flash * flash_strength;
	float a = max(covered * (1.0 - revealed), fa);

	// 波前亮边：铺开段在亮幕外沿、收回段在空洞内沿
	float ring = 0.0;
	vec2 ring_dir = vec2(1.0, 0.0);
	if (covered > 0.0) {
		float w = 1.0 - smoothstep(0.0, rim_width, abs(dc - cover));
		if (w > ring) {
			ring = w;
			ring_dir = normalize(px - cover_center * screen_size + vec2(0.0001));
		}
	}
	if (reveal > 0.0) {
		float w = 1.0 - smoothstep(0.0, rim_width, abs(dr - reveal));
		if (w > ring) {
			ring = w;
			ring_dir = normalize(px - reveal_center * screen_size + vec2(0.0001));
		}
	}

	// 亮幕颜色：波前最亮，身后随距离沉为柔和的青蓝（整屏不死白、不刺眼）
	float depth = cover > 0.0 ? clamp((cover - dc) / gradient_len, 0.0, 1.0) : 1.0;
	vec3 rgb = mix(rim_color.rgb, fill_color.rgb, depth);
	rgb = mix(rgb, rim_color.rgb, max(ring * 0.7, fa * 0.5));
	// 波前折射：亮边处按径向轻微偏移采样屏幕，亮幕边缘像透镜一样透出场景
	if (ring > 0.01 && refract_strength > 0.0) {
		vec3 refr = texture(screen_tex, SCREEN_UV + ring_dir * ring * refract_strength).rgb;
		rgb = mix(rgb, refr, ring * 0.4);
	}
	COLOR = vec4(rgb, a * fill_color.a);
}
