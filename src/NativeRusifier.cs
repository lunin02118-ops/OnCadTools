using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: AssemblyVersion("0.0.0.0")]
internal class NativeRusifier
{
	private delegate bool EnumProc(IntPtr h, IntPtr l);

	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private const uint BM_CLICK = 245u;
	private const uint WM_CLOSE = 16u;
	private const int GWL_STYLE = -16;
	private const int BS_DEFPUSHBUTTON = 1;
	private const uint SWP_NOMOVE = 2u;
	private const uint SWP_NOZORDER = 4u;
	private const uint SWP_NOACTIVATE = 16u;

	private static Dictionary<string, string> dict = new Dictionary<string, string>();
	private static readonly object gate = new object();
	private static string dictPath;
	private static string logPath;
	private static int replaced = 0;
	private static readonly bool DisableAutoClose = Environment.GetEnvironmentVariable("ONCADTOOLS_NO_AUTOCLOSE") == "1";
	private static string lastDlgSig;

	private static readonly HashSet<string> SkipValueTokens = new HashSet<string>
	{
		"厚", "$修改日期", "显示列方案1", "方通", "方管", "角铁", "角钢", "型钢", "上色", "Dashed虚线",
		"直选-off"
	};

	private static readonly string[] DataFileMarkers = new string[]
	{
		".sld", ".drwdot", ".slddrt", ".prtdot", ".asmdot", ".sldprt", ".sldasm", ".slddrw",
		".sldbomtbt", ".sldholtbt", ".sldwldtbt", ".sldrevtbt", ".sldfvt", ".sldblk", ".sldmat",
		".swp", ".ini", ".xls", ".xlsx", ".xlt", ".txt", ".p2m", ".pdf", ".dwg", ".dxf"
	};

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc cb, IntPtr l);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

	[DllImport("user32.dll")]
	private static extern IntPtr GetParent(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool SetWindowTextW(IntPtr h, string t);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

	[DllImport("user32.dll")]
	private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

	[DllImport("user32.dll")]
	private static extern bool UpdateWindow(IntPtr hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr OpenWindowStationW(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

	[DllImport("user32.dll")]
	private static extern bool SetProcessWindowStation(IntPtr hWinSta);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

	[DllImport("user32.dll")]
	private static extern bool SetThreadDesktop(IntPtr hDesktop);

	[DllImport("user32.dll")]
	private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumProc lpfn, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern int GetWindowLongW(IntPtr h, int index);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	private static bool HasCjk(string s)
	{
		foreach (char c in s)
		{
			if (c >= '一' && c <= '鿿')
			{
				return true;
			}
		}
		return false;
	}

	private static bool LooksLikeDataFilePath(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return false;
		}
		string t = s.Trim();
		if (t.IndexOf('\\') >= 0 || t.IndexOf('/') >= 0)
		{
			return true;
		}
		if (t.StartsWith(@"\\") || (t.Length >= 2 && char.IsLetter(t[0]) && t[1] == ':'))
		{
			return true;
		}
		for (int i = 0; i < DataFileMarkers.Length; i++)
		{
			if (t.IndexOf(DataFileMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsCjkCodePoint(char c)
	{
		if (c < '一' || c > '鿿')
		{
			if (c >= '\u3000')
			{
				return c <= '〿';
			}
			return false;
		}
		return true;
	}

	private static bool IsMixedCjkText(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return false;
		}
		bool flag = false;
		bool result = false;
		foreach (char c in s)
		{
			if (IsCjkCodePoint(c))
			{
				flag = true;
			}
			else if (!char.IsWhiteSpace(c))
			{
				result = true;
			}
		}
		if (flag)
		{
			return result;
		}
		return false;
	}

	private static string NormalizeTitleKey(string t)
	{
		if (string.IsNullOrEmpty(t))
		{
			return null;
		}
		string text = t;
		if (text.EndsWith("瓯南工具箱"))
		{
			text = text.Substring(0, text.Length - "瓯南工具箱".Length).Trim();
		}
		string text2 = Regex.Replace(text, "V\\d+(\\.\\d+)?--OnCadTools", "--OnCadTools");
		if (text2 != text)
		{
			text = text2.Trim();
		}
		if (!(text != t))
		{
			return null;
		}
		return text;
	}

	private static string TranslateCjkSegments(string t)
	{
		MatchCollection matchCollection = Regex.Matches(t, "[一-鿿\u3000-〿]+");
		StringBuilder stringBuilder = new StringBuilder();
		int num = 0;
		bool flag = false;
		foreach (Match item in matchCollection)
		{
			if (item.Index > num)
			{
				stringBuilder.Append(t, num, item.Index - num);
			}
			string value;
			if (dict.TryGetValue(item.Value, out value))
			{
				stringBuilder.Append(value);
				flag = true;
			}
			else
			{
				stringBuilder.Append(item.Value);
			}
			num = item.Index + item.Length;
		}
		if (!flag)
		{
			return null;
		}
		if (num < t.Length)
		{
			stringBuilder.Append(t, num, t.Length - num);
		}
		return stringBuilder.ToString();
	}

	private static string TranslateLine(string t)
	{
		string value;
		if (dict.TryGetValue(t, out value))
		{
			return value;
		}
		string text = NormalizeTitleKey(t);
		if (text != null && dict.TryGetValue(text, out value))
		{
			return value;
		}
		Match match = Regex.Match(t, "\\s*V\\d+(\\.\\d+)?$");
		if (match.Success)
		{
			string key = t.Substring(0, match.Index).Trim();
			string text2 = t.Substring(match.Index);
			if (dict.TryGetValue(key, out value))
			{
				return value + text2;
			}
		}
		foreach (KeyValuePair<string, string> item in dict)
		{
			if (item.Key.Length > 1 && item.Key.EndsWith("*"))
			{
				string text3 = item.Key.TrimEnd('*');
				if (t.StartsWith(text3))
				{
					return item.Value + t.Substring(text3.Length);
				}
			}
		}
		foreach (KeyValuePair<string, string> item2 in dict)
		{
			if (item2.Key.Length >= 4 && t.Contains(item2.Key))
			{
				string text4 = t.Replace(item2.Key, item2.Value);
				if (!HasCjk(text4))
				{
					return text4;
				}
			}
		}
		if (IsMixedCjkText(t))
		{
			return TranslateCjkSegments(t);
		}
		return null;
	}

	private static string Translate(string text)
	{
		if (LooksLikeDataFilePath(text))
		{
			return null;
		}
		if (text.IndexOf('\n') >= 0)
		{
			string[] array = text.Replace("\r\n", "\n").Split('\n');
			string[] array2 = new string[array.Length];
			bool flag = false;
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i].TrimEnd('\r');
				if (text2.Length == 0 || !HasCjk(text2))
				{
					array2[i] = text2;
					continue;
				}
				string text3 = TranslateLine(text2);
				if (text3 == null)
				{
					array2[i] = text2;
					continue;
				}
				array2[i] = text3;
				flag = true;
			}
			if (flag)
			{
				return string.Join("\r\n", array2);
			}
			return null;
		}
		string text4 = TranslateLine(text);
		if (text4 != null)
		{
			return text4;
		}
		string text5 = text;
		text5 = text5.Replace("图纸1", "Лист1").Replace("图纸", "Лист");
		if (text5 != text && !HasCjk(text5))
		{
			return text5;
		}
		return null;
	}

	private static void Log(string s)
	{
		lock (gate)
		{
			try
			{
				File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n", Encoding.UTF8);
			}
			catch
			{
			}
		}
	}

	private static bool IsTooltipWindow(IntPtr hWnd)
	{
		if (hWnd == IntPtr.Zero) return false;
		try
		{
			StringBuilder sb = new StringBuilder(64);
			GetClassNameW(hWnd, sb, 64);
			string cls = sb.ToString();
			if (cls.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			IntPtr hParent = GetParent(hWnd);
			if (hParent != IntPtr.Zero)
			{
				sb.Length = 0;
				GetClassNameW(hParent, sb, 64);
				string pcls = sb.ToString();
				if (pcls.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static void RusifyWindow(IntPtr h)
	{
		try
		{
			if (IsTooltipWindow(h))
			{
				return;
			}

			StringBuilder stringBuilder = new StringBuilder(512);
			GetWindowTextW(h, stringBuilder, 512);
			string text = stringBuilder.ToString().Trim();
			if (text.Length > 0 && !LooksLikeDataFilePath(text))
			{
				string value = null;
				if (!dict.TryGetValue(text, out value) && HasCjk(text))
				{
					value = Translate(text);
				}
				if (value != null && value != text && SetWindowTextW(h, value))
				{
					replaced++;
					try
					{
						InvalidateRect(h, IntPtr.Zero, true);
						UpdateWindow(h);
					}
					catch
					{
					}
					Log("WINDOW " + h + ": [" + text + "] -> [" + value + "]");
				}
			}
			EnumChildWindows(h, delegate(IntPtr ch, IntPtr l2)
			{
				if (IsTooltipWindow(ch))
				{
					return true;
				}

				StringBuilder stringBuilder2 = new StringBuilder(1024);
				GetWindowTextW(ch, stringBuilder2, 1024);
				string text2 = stringBuilder2.ToString().Trim();
				if (text2.Length == 0 || text2.Length > 1024)
				{
					return true;
				}
				StringBuilder stringBuilder3 = new StringBuilder(64);
				GetClassNameW(ch, stringBuilder3, 64);
				string text3 = stringBuilder3.ToString();
				if (LooksLikeDataFilePath(text2))
				{
					return true;
				}
				if (text3.ToUpper(CultureInfo.InvariantCulture).IndexOf("EDIT") >= 0 && SkipValueTokens.Contains(text2))
				{
					return true;
				}
				string value2 = null;
				if (!dict.TryGetValue(text2, out value2) && HasCjk(text2))
				{
					value2 = Translate(text2);
				}
				if (value2 != null && value2 != text2 && SetWindowTextW(ch, value2))
				{
					replaced++;
					try
					{
						InvalidateRect(ch, IntPtr.Zero, true);
						UpdateWindow(ch);
					}
					catch
					{
					}
					Log("CHILD " + ch + " of " + h + ": [" + text2 + "] -> [" + value2 + "]");
					try
					{
						RECT lpRect, lpRect2;
						if ((text3.IndexOf("STATIC", StringComparison.OrdinalIgnoreCase) >= 0 || text3 == "Static") && GetWindowRect(ch, out lpRect) && GetWindowRect(h, out lpRect2))
						{
							int num = lpRect.Right - lpRect.Left;
							int val = lpRect.Bottom - lpRect.Top;
							int num2 = Math.Max(num, value2.Length * 9 + 50);
							if (num2 > num)
							{
								SetWindowPos(ch, IntPtr.Zero, 0, 0, num2, Math.Max(val, 50), 22u);
							}
						}
					}
					catch
					{
					}
				}
				return true;
			}, IntPtr.Zero);
		}
		catch
		{
		}
	}

	private static void AutoContinueExceptionDialog(IntPtr h)
	{
		try
		{
			StringBuilder stringBuilder = new StringBuilder(256);
			GetWindowTextW(h, stringBuilder, 256);
			string text = stringBuilder.ToString();
			StringBuilder stringBuilder2 = new StringBuilder(64);
			GetClassNameW(h, stringBuilder2, 64);
			string text2 = stringBuilder2.ToString();
			if (text.IndexOf(".NET Framework", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Исключение", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Ошибка", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0 && !(text2 == "#32770") && text2.IndexOf("WindowsForms", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return;
			}
			bool isExDialog = false;
			if (text.IndexOf("Ошибка", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Исключение", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf(".NET Framework", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				isExDialog = true;
			}
			IntPtr btn = IntPtr.Zero;
			string defBtn = "";
			List<string> texts = new List<string>();
			EnumChildWindows(h, delegate(IntPtr ch, IntPtr l2)
			{
				StringBuilder stringBuilder3 = new StringBuilder(8192);
				GetWindowTextW(ch, stringBuilder3, 8192);
				string text4 = stringBuilder3.ToString();
				StringBuilder stringBuilder4 = new StringBuilder(64);
				GetClassNameW(ch, stringBuilder4, 64);
				string text5 = stringBuilder4.ToString();
				if (text4.IndexOf("Необрабатываемое", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Unhandled", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Текст исключения", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("NullReference", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Ссылка на объект", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("JIT", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Runtime exception", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Ошибка", StringComparison.OrdinalIgnoreCase) >= 0 || text4.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					isExDialog = true;
				}
				bool flag = text5.ToUpper(CultureInfo.InvariantCulture).IndexOf("BUTTON") >= 0;
				string text6 = text4.Replace("&", "").Trim();
				if (flag)
				{
					if (text6.IndexOf("Продолж", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						btn = ch;
					}
					else if (btn == IntPtr.Zero && text6.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						btn = ch;
					}
					else if (btn == IntPtr.Zero && (text6.Equals("OK", StringComparison.OrdinalIgnoreCase) || text6.Equals("ОК", StringComparison.OrdinalIgnoreCase) || text6.Equals("Ок", StringComparison.OrdinalIgnoreCase)))
					{
						btn = ch;
					}
					else if (btn == IntPtr.Zero && (text6.IndexOf("Закрыть", StringComparison.OrdinalIgnoreCase) >= 0 || text6.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						btn = ch;
					}
					else if (btn == IntPtr.Zero && (text6.Equals("Да", StringComparison.OrdinalIgnoreCase) || text6.Equals("Yes", StringComparison.OrdinalIgnoreCase)))
					{
						btn = ch;
					}
					if ((GetWindowLongW(ch, -16) & 1) != 0)
					{
						defBtn = text6;
						if (btn == IntPtr.Zero)
						{
							btn = ch;
						}
					}
				}
				if (text4.Trim().Length > 0)
				{
					texts.Add(text5 + ": [" + text4 + "]");
				}
				return true;
			}, IntPtr.Zero);
			if (isExDialog && btn == IntPtr.Zero)
			{
				EnumChildWindows(h, delegate(IntPtr ch, IntPtr l2)
				{
					StringBuilder stringBuilder3 = new StringBuilder(64);
					GetClassNameW(ch, stringBuilder3, 64);
					if (stringBuilder3.ToString().ToUpper(CultureInfo.InvariantCulture).IndexOf("BUTTON") >= 0)
					{
						StringBuilder stringBuilder4 = new StringBuilder(256);
						GetWindowTextW(ch, stringBuilder4, 256);
						string text4 = stringBuilder4.ToString().Replace("&", "").Trim();
						if (text4.IndexOf("Отмен", StringComparison.OrdinalIgnoreCase) < 0 && text4.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) < 0)
						{
							btn = ch;
							return false;
						}
					}
					return true;
				}, IntPtr.Zero);
			}
			if (!isExDialog)
			{
				return;
			}
			string text3 = h + "|" + defBtn + "|" + string.Join("|", texts.ToArray());
			if (text3 != lastDlgSig)
			{
				lastDlgSig = text3;
				Log("EXCEPTION DIALOG DETECTED hwnd=" + h + " title=[" + text + "] default button=[" + defBtn + "]");
				for (int num = 0; num < texts.Count; num++)
				{
					Log("  dlgtext: " + texts[num]);
				}
			}
			if (DisableAutoClose)
			{
				Log("Auto-close disabled via ONCADTOOLS_NO_AUTOCLOSE");
			}
			else if (btn != IntPtr.Zero)
			{
				SendMessageW(btn, 245u, IntPtr.Zero, IntPtr.Zero);
				Log("EXCEPTION DIALOG auto-continued via button click: hwnd=" + h);
			}
			else
			{
				SendMessageW(h, 16u, IntPtr.Zero, IntPtr.Zero);
				Log("EXCEPTION DIALOG auto-closed via WM_CLOSE: hwnd=" + h);
			}
		}
		catch
		{
		}
	}

	private static string ResolveDictPath()
	{
		string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native_dict.tsv");
		if (File.Exists(text))
		{
			return text;
		}
		return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\..\\native_dict.tsv"));
	}

	private static void LoadDict()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		if (!File.Exists(dictPath))
		{
			Log("dict not found: " + dictPath);
			return;
		}
		string[] array = File.ReadAllLines(dictPath);
		foreach (string text in array)
		{
			int num = text.IndexOf('\t');
			if (num > 0)
			{
				string text2 = text.Substring(0, num).Trim();
				string text3 = text.Substring(num + 1).Trim();
				if (text2.Length > 0 && text3.Length > 0 && !dictionary.ContainsKey(text2))
				{
					dictionary[text2] = text3;
				}
			}
		}
		dict = dictionary;
		Log("dict loaded: " + dict.Count + " pairs from " + dictPath);
	}

	private static IntPtr AttachToInteractiveDesktop()
	{
		try
		{
			IntPtr intPtr = OpenWindowStationW("winsta0", false, 895u);
			if (intPtr != IntPtr.Zero)
			{
				SetProcessWindowStation(intPtr);
			}
			IntPtr intPtr2 = OpenDesktopW("default", 0u, false, 511u);
			if (intPtr2 != IntPtr.Zero)
			{
				SetThreadDesktop(intPtr2);
				return intPtr2;
			}
		}
		catch
		{
		}
		return IntPtr.Zero;
	}

	private static void Main(string[] args)
	{
		if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
		{
			dictPath = args[0];
		}
		else
		{
			dictPath = ResolveDictPath();
		}
		string text = Path.GetDirectoryName(dictPath);
		if (string.IsNullOrEmpty(text))
		{
			text = AppDomain.CurrentDomain.BaseDirectory;
		}
		if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
		{
			logPath = args[1];
		}
		else
		{
			logPath = Path.Combine(text, "native_rusify.log");
		}
		try
		{
			string directoryName = Path.GetDirectoryName(logPath);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
		}
		catch
		{
		}
		LoadDict();
		FileSystemWatcher fileSystemWatcher = new FileSystemWatcher(text, "native_dict.tsv");
		int reloadBusy = 0;
		fileSystemWatcher.Changed += delegate
		{
			if (Interlocked.CompareExchange(ref reloadBusy, 1, 0) != 0)
			{
				return;
			}
			try
			{
				Thread.Sleep(200);
				LoadDict();
			}
			catch
			{
			}
			finally
			{
				Thread.Sleep(100);
				reloadBusy = 0;
			}
		};
		fileSystemWatcher.Error += delegate(object s, ErrorEventArgs e)
		{
			try
			{
				Log("dict watcher error: " + e.GetException().Message);
			}
			catch
			{
			}
		};
		fileSystemWatcher.EnableRaisingEvents = true;
		IntPtr intPtr = AttachToInteractiveDesktop();
		Log("=== NativeRusifier started ===" + (DisableAutoClose ? " [NO_AUTOCLOSE]" : " [AUTOCLOSE_ACTIVE]") + ((intPtr != IntPtr.Zero) ? " [DESKTOP_ATTACHED]" : ""));
		int num = 0;
		int num2 = 0;
		while (true)
		{
			try
			{
				if (intPtr == IntPtr.Zero)
				{
					intPtr = AttachToInteractiveDesktop();
				}
				HashSet<uint> pids = new HashSet<uint>();
				Process[] processesByName = Process.GetProcessesByName("SLDWORKS");
				foreach (Process process in processesByName)
				{
					pids.Add((uint)process.Id);
					process.Dispose();
				}
				if (pids.Count == 0)
				{
					if (++num2 >= 2000)
					{
						Log("no SLDWORKS for ~10 min, exiting");
						break;
					}
				}
				else
				{
					num2 = 0;
					HashSet<IntPtr> visited = new HashSet<IntPtr>();
					EnumProc enumProc = delegate(IntPtr h, IntPtr l)
					{
						if (visited.Add(h))
						{
							uint pid;
							GetWindowThreadProcessId(h, out pid);
							if (pids.Contains(pid))
							{
								RusifyWindow(h);
								AutoContinueExceptionDialog(h);
							}
						}
						return true;
					};
					if (intPtr != IntPtr.Zero)
					{
						EnumDesktopWindows(intPtr, enumProc, IntPtr.Zero);
					}
					EnumWindows(enumProc, IntPtr.Zero);
				}
			}
			catch (Exception ex)
			{
				Log("cycle ex: " + ex.Message);
			}
			if (++num % 3000 == 0)
			{
				Log("heartbeat, replaced total=" + replaced);
			}
			Thread.Sleep(300);
		}
	}
}
