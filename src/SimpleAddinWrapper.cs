using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

[assembly: AssemblyTitle("OnCadTools")]
[assembly: AssemblyProduct("OnCadTools")]
[assembly: AssemblyVersion("3.1.0.3")]
[assembly: AssemblyFileVersion("3.1.0.3")]
[assembly: ComVisible(true)]
[assembly: Guid("ca4d7f57-f642-4f3b-b230-0196885df001")]

namespace OnCadTools
{
    public static class CadLogger
    {
        private static readonly object _logLock = new object();
        private static readonly List<string> _logPaths = new List<string>();

        static CadLogger()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    _logPaths.Add(Path.Combine(baseDir, "wrapper_debug.log"));
                }
            }
            catch { }

            _logPaths.Add(@"D:\Program Files\OnCadTools\wrapper_debug.log");
            _logPaths.Add(@"D:\dev\OnCadTools\wrapper_debug.log");
        }

        public static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    string text = string.Format("[{0:HH:mm:ss.fff}] {1}\r\n", DateTime.Now, msg);
                    foreach (var path in _logPaths)
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(path);
                            if (Directory.Exists(dir))
                            {
                                File.AppendAllText(path, text, Encoding.UTF8);
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    public static class CadDict
    {
        private static readonly Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<string, string>> _sorted = new List<KeyValuePair<string, string>>();
        private static bool _loaded = false;
        private static readonly object _lock = new object();

        public static void Load(string dictPath)
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    string finalPath = dictPath;
                    if (!File.Exists(finalPath))
                    {
                        string[] searchPaths = new string[] {
                            dictPath,
                            @"D:\Program Files\OnCadTools\tools\native_dict.tsv",
                            @"D:\dev\OnCadTools\tools\native_dict.tsv",
                            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"tools\native_dict.tsv")
                        };
                        foreach (var p in searchPaths)
                        {
                            if (File.Exists(p)) { finalPath = p; break; }
                        }
                    }

                    if (File.Exists(finalPath))
                    {
                        foreach (var line in File.ReadAllLines(finalPath, Encoding.UTF8))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            int idx = line.IndexOf('\t');
                            if (idx > 0)
                            {
                                string k = line.Substring(0, idx).Trim();
                                string v = line.Substring(idx + 1).Trim();
                                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                                {
                                    _exact[k] = v;
                                    _sorted.Add(new KeyValuePair<string, string>(k, v));
                                }
                            }
                        }
                        _sorted.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                        _loaded = true;
                        CadLogger.Log("CadDict loaded: " + _exact.Count + " terms from " + finalPath);
                    }
                    else
                    {
                        CadLogger.Log("CadDict: native_dict.tsv not found at " + dictPath);
                    }
                }
                catch (Exception ex)
                {
                    CadLogger.Log("CadDict load exception: " + ex.Message);
                }
            }
        }

        public static string Translate(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string trimmed = s.Trim();
            string res;
            if (_exact.TryGetValue(trimmed, out res))
            {
                if (s.StartsWith(" ") && !res.StartsWith(" ")) res = " " + res;
                if (s.EndsWith(" ") && !res.EndsWith(" ")) res = res + " ";
                return res;
            }

            if (s.Contains("\n"))
            {
                var lines = s.Split(new char[] { '\n' });
                var trs = new string[lines.Length];
                for (int i = 0; i < lines.Length; i++) trs[i] = Translate(lines[i].TrimEnd('\r'));
                return string.Join("\r\n", trs);
            }

            // Check CJK characters
            bool hasCjk = false;
            foreach (char c in s)
            {
                if (c >= 0x4e00 && c <= 0x9fff) { hasCjk = true; break; }
            }
            if (!hasCjk) return s;

            // Substring replacement for compound sentences
            string cur = s;
            foreach (var kv in _sorted)
            {
                if (cur.Contains(kv.Key))
                {
                    cur = cur.Replace(kv.Key, kv.Value);
                    bool stillHasCjk = false;
                    foreach (char c in cur)
                    {
                        if (c >= 0x4e00 && c <= 0x9fff) { stillHasCjk = true; break; }
                    }
                    if (!stillHasCjk) break;
                }
            }
            return cur;
        }
    }

    [Guid("ca4d7f57-f642-4f3b-b230-0196885df002")]
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IOnCadWrapper
    {
        [DispId(1)]
        void RunCommand(string methodName);
        [DispId(2)]
        void TranslateForms();
        [DispId(3)]
        string GetVersion();
    }

    // --- Dynamic Proxies for Ribbon Command Translation ---
    public class CommandGroupProxy : RealProxy
    {
        private readonly CommandGroup _target;

        public CommandGroupProxy(CommandGroup target) : base(typeof(CommandGroup))
        {
            _target = target;
        }

        public override IMessage Invoke(IMessage msg)
        {
            var call = msg as IMethodCallMessage;
            if (call == null) return null;

            string methodName = call.MethodName;
            object[] args = (object[])call.Args.Clone();

            try
            {
                if (methodName == "AddCommandItem2")
                {
                    if (args.Length >= 9)
                    {
                        string oldName = args[0] as string;
                        string oldHint = args[2] as string;
                        string oldTip = args[3] as string;

                        if (oldName != null) args[0] = CadDict.Translate(oldName);
                        if (oldHint != null) args[2] = CadDict.Translate(oldHint);
                        if (oldTip != null) args[3] = CadDict.Translate(oldTip);

                        CadLogger.Log("AddCommandItem2: [" + oldName + "] -> [" + args[0] + "], ToolTip: [" + args[3] + "]");
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else if (methodName == "AddCommandItem")
                {
                    if (args.Length >= 8)
                    {
                        string oldName = args[0] as string;
                        string oldHint = args[2] as string;
                        string oldTip = args[3] as string;

                        if (oldName != null) args[0] = CadDict.Translate(oldName);
                        if (oldHint != null) args[2] = CadDict.Translate(oldHint);
                        if (oldTip != null) args[3] = CadDict.Translate(oldTip);

                        CadLogger.Log("AddCommandItem: [" + oldName + "] -> [" + args[0] + "]");
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else
                {
                    object res = call.MethodBase.Invoke(_target, args);
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
            }
            catch (Exception ex)
            {
                CadLogger.Log("CommandGroupProxy EX in " + methodName + ": " + ex.Message);
                object res = call.MethodBase.Invoke(_target, call.Args);
                return new ReturnMessage(res, call.Args, call.Args.Length, call.LogicalCallContext, call);
            }
        }
    }

    public class FlyoutGroupProxy : RealProxy
    {
        private readonly FlyoutGroup _target;

        public FlyoutGroupProxy(FlyoutGroup target) : base(typeof(FlyoutGroup))
        {
            _target = target;
        }

        public override IMessage Invoke(IMessage msg)
        {
            var call = msg as IMethodCallMessage;
            if (call == null) return null;

            string methodName = call.MethodName;
            object[] args = (object[])call.Args.Clone();

            try
            {
                if (methodName == "AddCommandItem")
                {
                    if (args.Length >= 1 && args[0] is string) args[0] = CadDict.Translate((string)args[0]);
                    if (args.Length >= 3 && args[2] is string) args[2] = CadDict.Translate((string)args[2]);
                    if (args.Length >= 4 && args[3] is string) args[3] = CadDict.Translate((string)args[3]);
                }
                object res = call.MethodBase.Invoke(_target, args);
                return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
            }
            catch (Exception ex)
            {
                CadLogger.Log("FlyoutGroupProxy EX in " + methodName + ": " + ex.Message);
                object res = call.MethodBase.Invoke(_target, call.Args);
                return new ReturnMessage(res, call.Args, call.Args.Length, call.LogicalCallContext, call);
            }
        }
    }

    public class CommandManagerProxy : RealProxy
    {
        private readonly CommandManager _target;

        public CommandManagerProxy(CommandManager target) : base(typeof(CommandManager))
        {
            _target = target;
        }

        public override IMessage Invoke(IMessage msg)
        {
            var call = msg as IMethodCallMessage;
            if (call == null) return null;

            string methodName = call.MethodName;
            object[] args = (object[])call.Args.Clone();

            try
            {
                if (methodName == "CreateCommandGroup2")
                {
                    if (args.Length >= 7)
                    {
                        string oldTitle = args[1] as string;
                        string oldTip = args[2] as string;
                        string oldHint = args[3] as string;

                        if (oldTitle != null) args[1] = CadDict.Translate(oldTitle);
                        if (oldTip != null) args[2] = CadDict.Translate(oldTip);
                        if (oldHint != null) args[3] = CadDict.Translate(oldHint);

                        args[5] = true; // IgnorePreviousVersion = true

                        CadLogger.Log("CreateCommandGroup2: [" + oldTitle + "] -> [" + args[1] + "], ToolTip: [" + args[2] + "]");
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    if (res is CommandGroup)
                    {
                        var grpProxy = new CommandGroupProxy((CommandGroup)res);
                        res = (CommandGroup)grpProxy.GetTransparentProxy();
                    }
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else if (methodName == "CreateCommandGroup")
                {
                    if (args.Length >= 4)
                    {
                        if (args[1] is string) args[1] = CadDict.Translate((string)args[1]);
                        if (args[2] is string) args[2] = CadDict.Translate((string)args[2]);
                        if (args[3] is string) args[3] = CadDict.Translate((string)args[3]);
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    if (res is CommandGroup)
                    {
                        var grpProxy = new CommandGroupProxy((CommandGroup)res);
                        res = (CommandGroup)grpProxy.GetTransparentProxy();
                    }
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else if (methodName == "AddCommandTab")
                {
                    if (args.Length >= 2 && args[1] is string)
                    {
                        string oldTab = (string)args[1];
                        args[1] = CadDict.Translate(oldTab);
                        CadLogger.Log("AddCommandTab: [" + oldTab + "] -> [" + args[1] + "]");
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else if (methodName == "GetCommandTab")
                {
                    object res = call.MethodBase.Invoke(_target, args);
                    if (res == null && args.Length >= 2 && args[1] is string)
                    {
                        string trTab = CadDict.Translate((string)args[1]);
                        if (trTab != (string)args[1])
                        {
                            args[1] = trTab;
                            res = call.MethodBase.Invoke(_target, args);
                        }
                    }
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else if (methodName == "CreateFlyoutGroup" || methodName == "CreateFlyoutGroup2")
                {
                    if (args.Length >= 4)
                    {
                        if (args[1] is string) args[1] = CadDict.Translate((string)args[1]);
                        if (args[2] is string) args[2] = CadDict.Translate((string)args[2]);
                        if (args[3] is string) args[3] = CadDict.Translate((string)args[3]);
                    }
                    object res = call.MethodBase.Invoke(_target, args);
                    if (res is FlyoutGroup)
                    {
                        var flProxy = new FlyoutGroupProxy((FlyoutGroup)res);
                        res = (FlyoutGroup)flProxy.GetTransparentProxy();
                    }
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
                else
                {
                    object res = call.MethodBase.Invoke(_target, args);
                    return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
                }
            }
            catch (Exception ex)
            {
                CadLogger.Log("CommandManagerProxy EX in " + methodName + ": " + ex.Message);
                object res = call.MethodBase.Invoke(_target, call.Args);
                return new ReturnMessage(res, call.Args, call.Args.Length, call.LogicalCallContext, call);
            }
        }
    }

    public class SwAppProxy : RealProxy
    {
        private readonly SldWorks _target;
        private readonly CommandManager _wrappedCmdMgr;

        public SwAppProxy(SldWorks target, CommandManager wrappedCmdMgr) : base(typeof(SldWorks))
        {
            _target = target;
            _wrappedCmdMgr = wrappedCmdMgr;
        }

        public override IMessage Invoke(IMessage msg)
        {
            var call = msg as IMethodCallMessage;
            if (call == null) return null;

            try
            {
                if (call.MethodName == "GetCommandManager")
                {
                    CadLogger.Log("SwAppProxy.GetCommandManager intercepted -> returning proxy CmdMgr");
                    return new ReturnMessage(_wrappedCmdMgr, null, 0, call.LogicalCallContext, call);
                }
                object[] args = call.Args;
                object res = call.MethodBase.Invoke(_target, args);
                return new ReturnMessage(res, args, args.Length, call.LogicalCallContext, call);
            }
            catch (Exception ex)
            {
                CadLogger.Log("SwAppProxy EX in " + call.MethodName + ": " + ex.Message);
                object res = call.MethodBase.Invoke(_target, call.Args);
                return new ReturnMessage(res, call.Args, call.Args.Length, call.LogicalCallContext, call);
            }
        }
    }

    [Guid("03412ba8-10f6-4d51-ac38-4937ce7bea5f")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IOnCadWrapper))]
    public class ZZZAddin : ISwAddin, IOnCadWrapper
    {
        private static bool _detoursInstalled = false;
        private static object _realAddin = null;
        private static MethodInfo _realConnect = null;
        private static MethodInfo _realDisconnect = null;
        private static object _thisSw = null;
        private static int _cookie = 0;
        private static System.Threading.Timer _watchdogTimer = null;

        // AddCommandMgr detour state
        private static byte[] _origAddCmdMgrBytes = null;
        private static byte[] _jmpAddCmdMgrBytes = null;
        private static IntPtr _pOrigAddCmdMgr = IntPtr.Zero;
        private static MethodInfo _origAddCmdMgr = null;
        private static MethodInfo _replAddCmdMgr = null;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public string GetVersion()
        {
            return "OnCadTools v3.1.0.3 (Russian Pro)";
        }

        public void TranslateForms()
        {
            SweepTranslate();
        }

        public void RunCommand(string methodName)
        {
            CadLogger.Log("IOnCadWrapper.RunCommand: " + methodName);
            RunCommandStatic(methodName);
        }

        // --- Detour Installer ---
        private static void InstallDetour(MethodInfo origMethod, MethodInfo replMethod)
        {
            if (origMethod == null || replMethod == null) return;
            try
            {
                RuntimeHelpers.PrepareMethod(origMethod.MethodHandle);
                RuntimeHelpers.PrepareMethod(replMethod.MethodHandle);

                IntPtr pOrig = origMethod.MethodHandle.GetFunctionPointer();
                IntPtr pRepl = replMethod.MethodHandle.GetFunctionPointer();

                byte[] jmp;
                if (IntPtr.Size == 8)
                {
                    jmp = new byte[12];
                    jmp[0] = 0x48; jmp[1] = 0xB8;
                    Buffer.BlockCopy(BitConverter.GetBytes(pRepl.ToInt64()), 0, jmp, 2, 8);
                    jmp[10] = 0xFF; jmp[11] = 0xE0;
                }
                else
                {
                    jmp = new byte[5];
                    jmp[0] = 0xE9;
                    int rel = (int)(pRepl.ToInt64() - pOrig.ToInt64() - 5);
                    Buffer.BlockCopy(BitConverter.GetBytes(rel), 0, jmp, 1, 4);
                }

                uint oldProt;
                VirtualProtect(pOrig, (UIntPtr)jmp.Length, 0x40 /*PAGE_EXECUTE_READWRITE*/, out oldProt);
                Marshal.Copy(jmp, 0, pOrig, jmp.Length);
                VirtualProtect(pOrig, (UIntPtr)jmp.Length, oldProt, out oldProt);
            }
            catch (Exception ex)
            {
                CadLogger.Log("InstallDetour error: " + ex.Message);
            }
        }

        private static void InstallAllDetours(Assembly coreAsm)
        {
            if (_detoursInstalled) return;
            try
            {
                // 1. Permanent Activation Detour
                var tAddin = coreAsm.GetType("OnCadTools.ZZZAddin");
                var origCheck = tAddin.GetMethod("CheckRegistry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var replCheck = typeof(ZZZAddin).GetMethod("CheckRegistry_Detour", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                InstallDetour(origCheck, replCheck);
                CadLogger.Log("Installed activation detour");

                // 2. AddCommandMgr Detour (Ribbon buttons and tooltips translation)
                _origAddCmdMgr = tAddin.GetMethod("AddCommandMgr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _replAddCmdMgr = typeof(ZZZAddin).GetMethod("AddCommandMgr_Detour", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_origAddCmdMgr != null && _replAddCmdMgr != null)
                {
                    RuntimeHelpers.PrepareMethod(_origAddCmdMgr.MethodHandle);
                    RuntimeHelpers.PrepareMethod(_replAddCmdMgr.MethodHandle);

                    _pOrigAddCmdMgr = _origAddCmdMgr.MethodHandle.GetFunctionPointer();
                    IntPtr pRepl = _replAddCmdMgr.MethodHandle.GetFunctionPointer();

                    _origAddCmdMgrBytes = new byte[12];
                    Marshal.Copy(_pOrigAddCmdMgr, _origAddCmdMgrBytes, 0, 12);

                    _jmpAddCmdMgrBytes = new byte[12];
                    _jmpAddCmdMgrBytes[0] = 0x48; _jmpAddCmdMgrBytes[1] = 0xB8;
                    Buffer.BlockCopy(BitConverter.GetBytes(pRepl.ToInt64()), 0, _jmpAddCmdMgrBytes, 2, 8);
                    _jmpAddCmdMgrBytes[10] = 0xFF; _jmpAddCmdMgrBytes[11] = 0xE0;

                    uint oldProt;
                    VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, 0x40, out oldProt);
                    Marshal.Copy(_jmpAddCmdMgrBytes, 0, _pOrigAddCmdMgr, 12);
                    VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, oldProt, out oldProt);

                    CadLogger.Log("Installed AddCommandMgr detour");
                }

                // 3. Language View Detours
                var tLang = coreAsm.GetType("OnCadTools.language");
                if (tLang != null)
                {
                    InstallLangDetour(tLang, "FrontView", "FrontView_Detour");
                    InstallLangDetour(tLang, "BackView", "BackView_Detour");
                    InstallLangDetour(tLang, "LeftView", "LeftView_Detour");
                    InstallLangDetour(tLang, "RightView", "RightView_Detour");
                    InstallLangDetour(tLang, "TopView", "TopView_Detour");
                    InstallLangDetour(tLang, "BottomView", "BottomView_Detour");
                    InstallLangDetour(tLang, "Isometric", "Isometric_Detour");
                    InstallLangDetour(tLang, "Trimetric", "Trimetric_Detour");
                    InstallLangDetour(tLang, "Dimetric", "Dimetric_Detour");
                    InstallLangDetour(tLang, "FlatPatternView", "FlatPatternView_Detour");
                    InstallLangDetour(tLang, "CurrentModelView", "CurrentModelView_Detour");
                    InstallLangDetour(tLang, "Standard3View", "Standard3View_Detour");
                    InstallLangDetour(tLang, "DrawingView", "DrawingView_Detour");
                    CadLogger.Log("Installed language view detours");
                }

                _detoursInstalled = true;
            }
            catch (Exception ex)
            {
                CadLogger.Log("InstallAllDetours EX: " + ex.Message);
            }
        }

        private static void InstallLangDetour(Type tLang, string origName, string replName)
        {
            try
            {
                var orig = tLang.GetMethod(origName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var repl = typeof(ZZZAddin).GetMethod(replName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (orig != null && repl != null)
                {
                    InstallDetour(orig, repl);
                }
            }
            catch { }
        }

        // --- Detour for AddCommandMgr ---
        public static void AddCommandMgr_Detour(object addin)
        {
            CadLogger.Log("AddCommandMgr_Detour triggered!");
            try
            {
                // 1. Temporarily restore original code bytes of AddCommandMgr
                uint oldProt;
                VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, 0x40, out oldProt);
                Marshal.Copy(_origAddCmdMgrBytes, 0, _pOrigAddCmdMgr, 12);
                VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, oldProt, out oldProt);

                Type tAddin = addin.GetType();
                FieldInfo fCmdMgr = tAddin.GetField("iCmdMgr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fSwApp = tAddin.GetField("iSwApp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                CommandManager realCmdMgr = fCmdMgr != null ? fCmdMgr.GetValue(addin) as CommandManager : null;
                SldWorks realSwApp = fSwApp != null ? fSwApp.GetValue(addin) as SldWorks : null;

                CommandManager proxyCmdMgr = null;
                if (realCmdMgr != null)
                {
                    var cmdMgrProxy = new CommandManagerProxy(realCmdMgr);
                    proxyCmdMgr = (CommandManager)cmdMgrProxy.GetTransparentProxy();
                    fCmdMgr.SetValue(addin, proxyCmdMgr);
                    CadLogger.Log("Attached CommandManagerProxy to addin.iCmdMgr");
                }

                SldWorks proxySwApp = null;
                if (realSwApp != null && proxyCmdMgr != null)
                {
                    var swProxy = new SwAppProxy(realSwApp, proxyCmdMgr);
                    proxySwApp = (SldWorks)swProxy.GetTransparentProxy();
                    fSwApp.SetValue(addin, proxySwApp);
                    CadLogger.Log("Attached SwAppProxy to addin.iSwApp");
                }

                // 2. Invoke original AddCommandMgr with proxies active
                try
                {
                    _origAddCmdMgr.Invoke(addin, null);
                    CadLogger.Log("Original AddCommandMgr executed successfully with proxies!");
                }
                catch (Exception ex)
                {
                    CadLogger.Log("Original AddCommandMgr execution EX: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                }
                finally
                {
                    // 3. Restore real COM references
                    if (realCmdMgr != null && fCmdMgr != null)
                    {
                        fCmdMgr.SetValue(addin, realCmdMgr);
                    }
                    if (realSwApp != null && fSwApp != null)
                    {
                        fSwApp.SetValue(addin, realSwApp);
                    }
                }
            }
            catch (Exception ex)
            {
                CadLogger.Log("AddCommandMgr_Detour EX: " + ex.Message);
            }
            finally
            {
                // 4. Re-install detour for future calls
                if (_pOrigAddCmdMgr != IntPtr.Zero && _jmpAddCmdMgrBytes != null)
                {
                    uint oldProt;
                    VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, 0x40, out oldProt);
                    Marshal.Copy(_jmpAddCmdMgrBytes, 0, _pOrigAddCmdMgr, 12);
                    VirtualProtect(_pOrigAddCmdMgr, (UIntPtr)12, oldProt, out oldProt);
                }
            }
        }

        // --- Permanent Activation Detour ---
        public static void CheckRegistry_Detour(object addin, out bool b超前超期, bool bReport)
        {
            b超前超期 = false;
            var t = addin.GetType();
            t.GetField("bRegistry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            t.GetField("bTry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, false);
            t.GetField("validDay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 99999);
            t.GetField("rNum", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, "181571K8G42I888888888888");
            t.GetField("nRegistry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(addin, 1);
            t.GetField("registerID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(addin, 1);
        }

        // --- Standard View Detours ---
        private static bool IsRu(string l) { return l != null && l.ToLower().Contains("russian"); }
        private static bool IsCn(string l) { return l != null && l.ToLower().Contains("chinese"); }

        public static string FrontView_Detour(string l) { return IsRu(l) ? "*Спереди" : (IsCn(l) ? "*前视" : "*Front"); }
        public static string BackView_Detour(string l) { return IsRu(l) ? "*Сзади" : (IsCn(l) ? "*后视" : "*Back"); }
        public static string LeftView_Detour(string l) { return IsRu(l) ? "*Слева" : (IsCn(l) ? "*左视" : "*Left"); }
        public static string RightView_Detour(string l) { return IsRu(l) ? "*Справа" : (IsCn(l) ? "*右视" : "*Right"); }
        public static string TopView_Detour(string l) { return IsRu(l) ? "*Сверху" : (IsCn(l) ? "*俯视" : "*Top"); }
        public static string BottomView_Detour(string l) { return IsRu(l) ? "*Снизу" : (IsCn(l) ? "*仰视" : "*Bottom"); }
        public static string Isometric_Detour(string l) { return IsRu(l) ? "*Изометрия" : (IsCn(l) ? "*等轴测" : "*Isometric"); }
        public static string Trimetric_Detour(string l) { return IsRu(l) ? "*Триметрия" : (IsCn(l) ? "*三轴测" : "*Trimetric"); }
        public static string Dimetric_Detour(string l) { return IsRu(l) ? "*Диметрия" : (IsCn(l) ? "*二轴测" : "*Dimetric"); }
        public static string FlatPatternView_Detour(string l) { return IsRu(l) ? "*Развертка" : (IsCn(l) ? "*平板" : "*Flat pattern"); }
        public static string CurrentModelView_Detour(string l) { return IsRu(l) ? "*Текущий вид" : (IsCn(l) ? "*当前视图" : "*Current"); }
        public static string Standard3View_Detour(string l) { return IsRu(l) ? "3 стандартных вида" : (IsCn(l) ? "标准3视图" : "Standard 3 View"); }
        public static string DrawingView_Detour(string l) { return IsRu(l) ? "Чертежный вид" : (IsCn(l) ? "工程图" : "Drawing View"); }

        // --- Comprehensive In-Process UI Translation ---
        public static void TranslateControl(Control c)
        {
            if (c == null) return;
            try
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    string tr = CadDict.Translate(c.Text);
                    if (tr != c.Text) c.Text = tr;
                }

                // DataGridView: Columns, Headers, Tooltips, Combos
                var dgv = c as DataGridView;
                if (dgv != null)
                {
                    foreach (DataGridViewColumn col in dgv.Columns)
                    {
                        if (!string.IsNullOrEmpty(col.HeaderText))
                        {
                            string tr = CadDict.Translate(col.HeaderText);
                            if (tr != col.HeaderText) col.HeaderText = tr;
                        }
                        if (!string.IsNullOrEmpty(col.ToolTipText))
                        {
                            string tr = CadDict.Translate(col.ToolTipText);
                            if (tr != col.ToolTipText) col.ToolTipText = tr;
                        }
                        var cboCol = col as DataGridViewComboBoxColumn;
                        if (cboCol != null)
                        {
                            for (int i = 0; i < cboCol.Items.Count; i++)
                            {
                                if (cboCol.Items[i] is string)
                                {
                                    string tr = CadDict.Translate((string)cboCol.Items[i]);
                                    if (tr != (string)cboCol.Items[i]) cboCol.Items[i] = tr;
                                }
                            }
                        }
                    }
                }

                // ComboBox items
                var cbo = c as ComboBox;
                if (cbo != null)
                {
                    for (int i = 0; i < cbo.Items.Count; i++)
                    {
                        if (cbo.Items[i] is string)
                        {
                            string tr = CadDict.Translate((string)cbo.Items[i]);
                            if (tr != (string)cbo.Items[i]) cbo.Items[i] = tr;
                        }
                    }
                }

                // ListBox items
                var lb = c as ListBox;
                if (lb != null)
                {
                    for (int i = 0; i < lb.Items.Count; i++)
                    {
                        if (lb.Items[i] is string)
                        {
                            string tr = CadDict.Translate((string)lb.Items[i]);
                            if (tr != (string)lb.Items[i]) lb.Items[i] = tr;
                        }
                    }
                }

                // TabControl pages
                var tc = c as TabControl;
                if (tc != null)
                {
                    foreach (TabPage page in tc.TabPages)
                    {
                        if (!string.IsNullOrEmpty(page.Text))
                        {
                            string tr = CadDict.Translate(page.Text);
                            if (tr != page.Text) page.Text = tr;
                        }
                        if (!string.IsNullOrEmpty(page.ToolTipText))
                        {
                            string tr = CadDict.Translate(page.ToolTipText);
                            if (tr != page.ToolTipText) page.ToolTipText = tr;
                        }
                    }
                }

                // ToolStrips, MenuStrips, ContextMenus
                var ts = c as ToolStrip;
                if (ts != null)
                {
                    TranslateToolStripItems(ts.Items);
                }

                if (c.ContextMenuStrip != null)
                {
                    TranslateToolStripItems(c.ContextMenuStrip.Items);
                }

                // Children
                foreach (Control child in c.Controls)
                {
                    TranslateControl(child);
                }
            }
            catch { }
        }

        private static void TranslateToolStripItems(ToolStripItemCollection items)
        {
            if (items == null) return;
            foreach (ToolStripItem item in items)
            {
                try
                {
                    if (!string.IsNullOrEmpty(item.Text))
                    {
                        string tr = CadDict.Translate(item.Text);
                        if (tr != item.Text) item.Text = tr;
                    }
                    if (!string.IsNullOrEmpty(item.ToolTipText))
                    {
                        string tr = CadDict.Translate(item.ToolTipText);
                        if (tr != item.ToolTipText) item.ToolTipText = tr;
                    }
                    var drop = item as ToolStripDropDownItem;
                    if (drop != null && drop.DropDownItems.Count > 0)
                    {
                        TranslateToolStripItems(drop.DropDownItems);
                    }
                }
                catch { }
            }
        }

        private static bool IsTooltipHwnd(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(64);
                GetClassName(hWnd, sb, 64);
                string cls = sb.ToString();
                if (cls.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                IntPtr hParent = GetParent(hWnd);
                if (hParent != IntPtr.Zero)
                {
                    sb.Length = 0;
                    GetClassName(hParent, sb, 64);
                    string pcls = sb.ToString();
                    if (pcls.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static void TranslateHwnd(IntPtr hWnd)
        {
            try
            {
                // NEVER touch tooltip windows via Win32 SetWindowText!
                if (IsTooltipHwnd(hWnd)) return;

                var sb = new StringBuilder(512);
                if (GetWindowText(hWnd, sb, 512) > 0)
                {
                    string cur = sb.ToString();
                    bool hasCjk = false;
                    foreach (char ch in cur)
                    {
                        if (ch >= 0x4e00 && ch <= 0x9fff) { hasCjk = true; break; }
                    }
                    if (hasCjk)
                    {
                        string tr = CadDict.Translate(cur);
                        if (tr != cur)
                        {
                            SetWindowText(hWnd, tr);
                        }
                    }
                }
            }
            catch { }
        }

        public static void SweepTranslate()
        {
            try
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

                // 1. Sweep OpenForms
                foreach (Form f in Application.OpenForms)
                {
                    if (f != null && !f.IsDisposed)
                    {
                        if (f.InvokeRequired)
                        {
                            f.BeginInvoke(new Action(() => TranslateControl(f)));
                        }
                        else
                        {
                            TranslateControl(f);
                        }
                    }
                }

                // 2. Sweep all Win32 and non-toplevel windows (TaskPaneForm, etc.)
                EnumWindows((hWnd, lParam) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == myPid)
                    {
                        Control ctrl = Control.FromHandle(hWnd);
                        if (ctrl != null)
                        {
                            if (ctrl.InvokeRequired) ctrl.BeginInvoke(new Action(() => TranslateControl(ctrl)));
                            else TranslateControl(ctrl);
                        }
                        else
                        {
                            TranslateHwnd(hWnd);
                        }

                        EnumChildWindows(hWnd, (hChild, lP) =>
                        {
                            Control childCtrl = Control.FromHandle(hChild);
                            if (childCtrl != null)
                            {
                                if (childCtrl.InvokeRequired) childCtrl.BeginInvoke(new Action(() => TranslateControl(childCtrl)));
                                else TranslateControl(childCtrl);
                            }
                            else
                            {
                                TranslateHwnd(hChild);
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public static void TranslateCommandManager(object thisSW, int cookie)
        {
            try
            {
                ISldWorks swApp = thisSW as ISldWorks;
                if (swApp == null) return;
                ICommandManager cmdMgr = swApp.GetCommandManager(cookie);
                if (cmdMgr == null) return;

                // 1. Translate CommandTabs for Part (1), Assembly (2), Drawing (3)
                for (int dt = 1; dt <= 3; dt++)
                {
                    try
                    {
                        object tabsObj = cmdMgr.CommandTabs(dt);
                        if (tabsObj is object[])
                        {
                            foreach (object t in (object[])tabsObj)
                            {
                                CommandTab tab = t as CommandTab;
                                if (tab != null && !string.IsNullOrEmpty(tab.Name))
                                {
                                    string tr = CadDict.Translate(tab.Name);
                                    if (tr != tab.Name)
                                    {
                                        tab.Name = tr;
                                        CadLogger.Log("Translated CommandTab: " + tab.Name + " -> " + tr);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                CadLogger.Log("TranslateCommandManager EX: " + ex.Message);
            }
        }

        public static void CleanCachedToolbars()
        {
            try
            {
                string[] contexts = new string[] { "Custom API Toolbars", @"CommandManager\PartContext", @"CommandManager\AssyContext", @"CommandManager\DrawContext" };
                foreach (string ctx in contexts)
                {
                    try
                    {
                        string path = @"Software\SolidWorks\SOLIDWORKS 2025\User Interface\" + ctx;
                        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, true))
                        {
                            if (key != null)
                            {
                                foreach (string sub in key.GetSubKeyNames())
                                {
                                    using (var sk = key.OpenSubKey(sub))
                                    {
                                        if (sk != null)
                                        {
                                            object mod = sk.GetValue("ModuleName");
                                            object refN = sk.GetValue("RefName");
                                            if ((mod != null && mod.ToString().IndexOf("03412BA8-10F6-4D51-AC38-4937CE7BEA5F", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                (refN != null && refN.ToString().IndexOf("OnCadTools", StringComparison.OrdinalIgnoreCase) >= 0))
                                            {
                                                key.DeleteSubKeyTree(sub, false);
                                                CadLogger.Log("Cleaned cached registry entry: " + ctx + "\\" + sub);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static Button FindButton(Control parent, string keyword)
        {
            if (parent == null) return null;
            foreach (Control c in parent.Controls)
            {
                var btn = c as Button;
                if (btn != null)
                {
                    if (btn.Text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (keyword == "MultiSolidCreateDraw" && (btn.Text.Contains("многотел") || btn.Text.Contains("多体出图"))))
                    {
                        return btn;
                    }
                }
                var sub = FindButton(c, keyword);
                if (sub != null) return sub;
            }
            return null;
        }

        public static void RunCommandStatic(string methodName)
        {
            CadLogger.Log("RunCommandStatic: " + methodName);
            try
            {
                // First search open forms for matching button
                foreach (Form f in Application.OpenForms)
                {
                    if (f != null && !f.IsDisposed)
                    {
                        var btn = FindButton(f, methodName);
                        if (btn != null)
                        {
                            CadLogger.Log("Found button '" + btn.Text + "' for " + methodName + ", invoking PerformClick()");
                            btn.BeginInvoke(new Action(() => btn.PerformClick()));
                            return;
                        }
                    }
                }

                if (_realAddin != null)
                {
                    var m = _realAddin.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    if (m != null)
                    {
                        var p = m.GetParameters();
                        if (p.Length == 0) m.Invoke(_realAddin, null);
                        else if (p.Length == 1 && p[0].ParameterType == typeof(int)) m.Invoke(_realAddin, new object[] { 0 });
                        CadLogger.Log("RunCommandStatic " + methodName + " invoked successfully");
                    }
                    else
                    {
                        CadLogger.Log("RunCommandStatic method not found: " + methodName);
                    }
                }
            }
            catch (Exception ex)
            {
                CadLogger.Log("RunCommandStatic EX: " + ex.Message);
            }
        }

        private static void StartWatchdog()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string triggerFile = Path.Combine(baseDir, "run_cmd.txt");

                _watchdogTimer = new System.Threading.Timer((state) =>
                {
                    // 1. External trigger file check
                    try
                    {
                        if (File.Exists(triggerFile))
                        {
                            string cmdName = File.ReadAllText(triggerFile).Trim();
                            try { File.Delete(triggerFile); } catch { }
                            CadLogger.Log("Command trigger received from file: " + cmdName);

                            var thread = new System.Threading.Thread(() =>
                            {
                                try { RunCommandStatic(cmdName); } catch { }
                            });
                            thread.SetApartmentState(ApartmentState.STA);
                            thread.Start();
                        }
                    }
                    catch { }

                    // 2. Periodic UI Sweep
                    try
                    {
                        SweepTranslate();
                    }
                    catch { }
                }, null, 500, 300);

                CadLogger.Log("In-process UI watchdog timer started (300ms interval)");
            }
            catch (Exception ex)
            {
                CadLogger.Log("StartWatchdog EX: " + ex.Message);
            }
        }

        static ZZZAddin()
        {
            CadLogger.Log("ZZZAddin static constructor started");
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                string p1 = Path.Combine(baseDir, name);
                if (File.Exists(p1)) return Assembly.LoadFile(p1);
                string p2 = Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS", name);
                if (File.Exists(p2)) return Assembly.LoadFile(p2);
                return null;
            };

            // Load master dictionary
            string dictPath = Path.Combine(baseDir, @"tools\native_dict.tsv");
            CadDict.Load(dictPath);

            // Clean cached toolbars in registry before startup
            CleanCachedToolbars();

            // Start in-process watchdog
            StartWatchdog();

            // Load Core and install all detours
            string corePath = Path.Combine(baseDir, "OnCadTools_Core.dll");
            if (File.Exists(corePath))
            {
                var coreAsm = Assembly.LoadFile(corePath);
                InstallAllDetours(coreAsm);

                var tReal = coreAsm.GetType("OnCadTools.ZZZAddin");
                _realAddin = Activator.CreateInstance(tReal);
                _realConnect = tReal.GetMethod("ConnectToSW");
                _realDisconnect = tReal.GetMethod("DisconnectFromSW");
                CadLogger.Log("Real addin created and methods bound");
            }
        }

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            CadLogger.Log("ConnectToSW called. Cookie: " + cookie);
            _thisSw = ThisSW;
            _cookie = cookie;

            // Clean cached registry toolbars so SolidWorks loads new translated definitions
            CleanCachedToolbars();

            // Start NativeRusifier watchdog in background if present
            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string watchdogExe = Path.Combine(baseDir, @"tools\NativeRusifier.exe");
                if (File.Exists(watchdogExe))
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("NativeRusifier");
                    if (procs.Length == 0)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(watchdogExe)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = baseDir
                        };
                        System.Diagnostics.Process.Start(psi);
                        CadLogger.Log("NativeRusifier watchdog started");
                    }
                }
            }
            catch { }

            if (_realConnect != null && _realAddin != null)
            {
                bool res = false;
                try
                {
                    res = (bool)_realConnect.Invoke(_realAddin, new object[] { ThisSW, cookie });
                    CadLogger.Log("Real ConnectToSW result: " + res);

                    // Translate CommandManager
                    TranslateCommandManager(ThisSW, cookie);

                    // Initial sweep
                    SweepTranslate();
                }
                catch (Exception ex)
                {
                    CadLogger.Log("Real ConnectToSW EX: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                }
                return res;
            }
            CadLogger.Log("ConnectToSW: realConnect or realAddin is null");
            return false;
        }

        public bool DisconnectFromSW()
        {
            CadLogger.Log("DisconnectFromSW called");
            if (_realDisconnect != null && _realAddin != null)
            {
                return (bool)_realDisconnect.Invoke(_realAddin, null);
            }
            return true;
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                Microsoft.Win32.RegistryKey hklm = Microsoft.Win32.Registry.LocalMachine;
                using (var addinKey = hklm.CreateSubKey(@"Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"))
                {
                    addinKey.SetValue(null, 1, Microsoft.Win32.RegistryValueKind.DWord);
                    addinKey.SetValue("Title", "OnCadTools");
                    addinKey.SetValue("Description", "OnCadTools SolidWorks Addin");
                }
                using (var startupKey = hklm.CreateSubKey(@"Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"))
                {
                    startupKey.SetValue(null, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }

                Microsoft.Win32.RegistryKey hkcu = Microsoft.Win32.Registry.CurrentUser;
                using (var addinKey = hkcu.CreateSubKey(@"Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"))
                {
                    addinKey.SetValue(null, 1, Microsoft.Win32.RegistryValueKind.DWord);
                    addinKey.SetValue("Title", "OnCadTools");
                    addinKey.SetValue("Description", "OnCadTools SolidWorks Addin");
                }
                using (var startupKey = hkcu.CreateSubKey(@"Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"))
                {
                    startupKey.SetValue(null, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", false);
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}", false);
            }
            catch { }
        }
    }
}
