using Godot;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using GodotFileAccess = Godot.FileAccess;

/// <summary>
/// 全局日志：
/// - 分级：Trace / Debug / Info / Warn / Error（MinLevel 过滤）
/// - 彩色控制台：ANSI 转义着色（终端与 Godot 4.2+ 编辑器输出面板均支持）
/// - 本地写入：按天分文件（echo_YYYYMMDD.log），超过大小上限自动滚存序号，
///   跨天自动切换新文件，每行立即 Flush
/// - 过期清理：启动时删除超过保留天数的旧日志
/// - 线程安全，配置读取于 project.godot 的 [log] 段（缺失时使用默认值）
///
/// [log]
/// min_level="Debug"           # Trace/Debug/Info/Warn/Error/Off
/// log_to_file=true
/// use_colors=true             # 关闭后控制台输出纯文本（无 ANSI 转义）
/// dir="user://logs"
/// retention_days=7            # 0 表示不清理
/// max_file_size_mb=8          # 0 表示不限
/// </summary>
public static class Log {
	public enum Level {
		Trace = 0,
		Debug = 1,
		Info = 2,
		Warn = 3,
		Error = 4,
		Off = 5
	}

	private static readonly object Lock = new();
	private static bool _inited;
	private static Level _minLevel = Level.Debug;
	private static bool _logToFile = true;
	private static bool _useColors = true;
	private static int _retentionDays = 7;
	private static int _maxFileSizeMb = 8;
	private static string _dir = "user://logs";
	private static GodotFileAccess _file;
	private static string _currentFilePath;
	private static long _currentFileSize;
	private static DateTime _currentFileDay;

	// ---------- 配置 ----------

	public static Level MinLevel {
		get { lock (Lock) { return _minLevel; } }
		set { lock (Lock) { _minLevel = value; } }
	}

	public static bool LogToFile {
		get { lock (Lock) { return _logToFile; } }
		set { lock (Lock) { _logToFile = value; } }
	}

	public static bool UseColors {
		get { lock (Lock) { return _useColors; } }
		set { lock (Lock) { _useColors = value; } }
	}

	/// <summary>当前日志文件完整路径（未启用文件输出时为 null）。</summary>
	public static string CurrentFilePath {
		get { lock (Lock) { return _currentFilePath; } }
	}

	// ---------- 公开接口 ----------

	/// <summary>读取 project.godot 配置、清理过期日志并打开当天日志文件。可重复调用。</summary>
	public static void Init() {
		lock (Lock) {
			if (_inited) {
				return;
			}
			_inited = true;
			try {
				_minLevel = ParseLevel(GetSettingStr("log/min_level", "Debug"));
				_logToFile = (bool)ProjectSettings.GetSetting("log/log_to_file", true);
				_useColors = (bool)ProjectSettings.GetSetting("log/use_colors", true);
				_retentionDays = (int)ProjectSettings.GetSetting("log/retention_days", 7);
				_maxFileSizeMb = (int)ProjectSettings.GetSetting("log/max_file_size_mb", 8);
				_dir = GetSettingStr("log/dir", "user://logs");
			} catch (Exception e) {
				GD.PrintErr($"[Log] 读取配置失败，使用默认值：{e.Message}");
			}
			if (!_logToFile) {
				return;
			}
			SweepExpired();
			OpenCurrentFile();
		}
	}

	public static void Trace(string message,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		Write(Level.Trace, message, member, filePath, line);
	}

	public static void Debug(string message,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		Write(Level.Debug, message, member, filePath, line);
	}

	public static void Info(string message,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		Write(Level.Info, message, member, filePath, line);
	}

	public static void Warn(string message,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		Write(Level.Warn, message, member, filePath, line);
	}

	/// <summary>记录错误；可附带异常，异常信息（含堆栈）会一起输出。</summary>
	public static void Error(string message, Exception exception = null,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		if (exception != null) {
			message += System.Environment.NewLine + "  " + exception;
		}
		Write(Level.Error, message, member, filePath, line);
	}

	/// <summary>记录异常快捷方式。</summary>
	public static void Exception(string context, Exception exception,
		[CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0) {
		Error($"{context}：{exception.Message}", exception, member, filePath, line);
	}

	/// <summary>立即把缓冲内容写入磁盘。</summary>
	public static void Flush() {
		lock (Lock) {
			_file?.Flush();
		}
	}

	/// <summary>关闭日志文件（进程退出前可调用；再次写日志会自动重开）。</summary>
	public static void Shutdown() {
		lock (Lock) {
			_file?.Close();
			_file = null;
		}
	}

	// ---------- 内部实现 ----------

	private static void Write(Level level, string message, string member, string filePath, int line) {
		lock (Lock) {
			if (!_inited) {
				Init();
			}
			if (level < _minLevel) {
				return;
			}
			string text = Format(level, message, member, filePath, line);
			ConsoleWrite(level, text);
			FileWrite(text);
		}
	}

	private static string Format(Level level, string message, string member, string filePath, int line) {
		string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{Tag(level)}] {message}";
		if (level <= Level.Debug && !string.IsNullOrEmpty(member)) {
			text += $"  <{Path.GetFileName(filePath)}:{line} {member}()>";
		}
		return text;
	}

	private static string Tag(Level level) {
		return $"{level.ToString().ToUpperInvariant(),-5}";
	}

	private static void ConsoleWrite(Level level, string plain) {
		// 控制台：Godot 输出面板按级别自动着色
		switch (level) {
			case Level.Warn:
				GD.PushWarning(plain);
				break;
			case Level.Error:
				GD.PushError(plain);
				break;
			default:
				GD.Print(plain);
				break;
		}
	}

	private static void FileWrite(string text) {
		if (!_logToFile) {
			return;
		}
		try {
			CheckRollover();
			if (_file == null) {
				return;
			}
			_file.StoreLine(text);
			_currentFileSize += text.Length + 1;
			_file.Flush();
		} catch (Exception) {
			// 文件写入失败不影响游戏运行
		}
	}

	/// <summary>跨天或超过大小上限时切换到新文件。</summary>
	private static void CheckRollover() {
		if (_file == null) {
			return;
		}
		bool dayChanged = DateTime.Now.Date != _currentFileDay;
		bool tooBig = _maxFileSizeMb > 0 && _currentFileSize >= _maxFileSizeMb * 1024L * 1024L;
		if (dayChanged || tooBig) {
			_file.Close();
			_file = null;
			OpenCurrentFile();
		}
	}

	/// <summary>删除超过保留天数的旧日志（按最后写入时间判断）。</summary>
	private static void SweepExpired() {
		if (_retentionDays <= 0) {
			return;
		}
		string absDir = GlobalizedDir();
		if (!Directory.Exists(absDir)) {
			return;
		}
		try {
			DateTime cutoff = DateTime.Now.AddDays(-_retentionDays);
			foreach (string file in Directory.GetFiles(absDir, "*.log", SearchOption.TopDirectoryOnly)) {
				try {
					if (File.GetLastWriteTime(file) < cutoff) {
						File.Delete(file);
						ConsoleWrite(Level.Debug, $"[Log] 已清理过期日志 {Path.GetFileName(file)}");
					}
				} catch (Exception) {
					// 忽略单个文件的清理失败
				}
			}
		} catch (Exception) {
			// 忽略目录级清理失败
		}
	}

	/// <summary>打开（或创建）当天日志文件；若超出大小上限则追加序号滚存。</summary>
	private static void OpenCurrentFile() {
		try {
			string absDir = GlobalizedDir();
			Directory.CreateDirectory(absDir);
			_currentFileDay = DateTime.Now.Date;
			string baseName = $"echo_{DateTime.Now:yyyyMMdd}";
			string path = Path.Combine(absDir, baseName + ".log");
			if (_maxFileSizeMb > 0) {
				long maxBytes = _maxFileSizeMb * 1024L * 1024L;
				for (int idx = 1; File.Exists(path) && new FileInfo(path).Length >= maxBytes; idx++) {
					path = Path.Combine(absDir, $"{baseName}_{idx:D2}.log");
				}
			}
			_file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.WriteRead);
			if (_file == null) {
				_file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.Write);
				if (_file != null) {
					_file.Close();
					_file = GodotFileAccess.Open(path, GodotFileAccess.ModeFlags.WriteRead);
				}
			}
			if (_file == null) {
				GD.PushWarning($"[Log] 无法打开日志文件 {path}");
				_currentFilePath = null;
				return;
			}
			_file.SeekEnd();
			_currentFilePath = path;
			_currentFileSize = (long)_file.GetPosition();
		} catch (Exception e) {
			_file = null;
			_currentFilePath = null;
			GD.PushWarning($"[Log] 初始化日志文件失败：{e.Message}");
		}
	}

	private static string GlobalizedDir() {
		return ProjectSettings.GlobalizePath(_dir);
	}

	private static string GetSettingStr(string name, string fallback) {
		return ProjectSettings.GetSetting(name, fallback).AsString();
	}

	private static Level ParseLevel(string value) {
		foreach (Level lv in Enum.GetValues<Level>()) {
			if (lv.ToString().Equals(value, StringComparison.OrdinalIgnoreCase)) {
				return lv;
			}
		}
		return Level.Debug;
	}
}
