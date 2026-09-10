using System;
using Godot;

/// <summary>
/// 资源路径解析：调用方只写“逻辑名”（相对路径），按类型自动定位到项目资源目录，扩展名可省略。
/// 本项目音频目录：<c>res://Assets/Audio/{bgm,se,voice}/</c>。
/// 也接受完整的 res:// 路径或带扩展名的路径；找不到返回 null（由调用方决定降级方式）。
/// </summary>
public static class ResourceResolver {
	/// <summary>音频资源根目录。</summary>
	public const string AudioRoot = "res://Assets/Audio";
	private static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3", ".opus" };

	/// <summary>
	/// 解析音频：kind ∈ { bgm, se, voice } → <c>Assets/Audio/&lt;kind&gt;/&lt;file&gt;</c>。
	/// file 也可写完整 res:// 路径、或以 "Assets/" 开头、或写成 "bgm/xxx"（自动去重前缀）。
	/// 返回 res:// 完整路径；找不到返回 null。
	/// </summary>
	public static string ResolveAudio(string kind, string file) {
		if (string.IsNullOrWhiteSpace(file)) {
			return null;
		}
		string rel = file.Replace('\\', '/').TrimStart('/');
		// 已是完整路径（res:// / user:// / Assets/ 开头）：直接按扩展名补全
		if (rel.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			|| rel.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
			|| rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) {
			return ResolveFile(rel, AudioExtensions);
		}
		// 允许写成 "bgm/xxx"：避免拼成 Assets/Audio/bgm/bgm/xxx
		string prefix = kind + "/";
		if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
			rel = rel[prefix.Length..];
		}
		return ResolveFile($"{AudioRoot}/{kind}/{rel}", AudioExtensions);
	}

	/// <summary>
	/// 按路径解析资源（res:// 前缀可省略）：文件原样存在直接返回；未指定扩展名时按 extensions 顺序补全尝试。
	/// </summary>
	public static string ResolveFile(string path, string[] extensions) {
		if (string.IsNullOrEmpty(path)) {
			return null;
		}
		string res = path.Replace('\\', '/');
		if (!res.StartsWith("res://") && !res.StartsWith("user://")) {
			res = "res://" + res.TrimStart('/');
		}
		if (Exists(res)) {
			return res;
		}
		if (!string.IsNullOrEmpty(res.GetExtension())) {
			return null; // 已指定扩展名但文件不存在
		}
		if (extensions != null) {
			foreach (string ext in extensions) {
				if (Exists(res + ext)) {
					return res + ext;
				}
			}
		}
		return null;
	}

	/// <summary>资源是否存在：已导入资源走 ResourceLoader，未导入的原始文件走 FileAccess（编辑器内）。</summary>
	public static bool Exists(string resPath) {
		return ResourceLoader.Exists(resPath) || Godot.FileAccess.FileExists(resPath);
	}
}
