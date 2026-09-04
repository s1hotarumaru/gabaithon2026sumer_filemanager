using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileOrganizer.Widgets.QuickLook;

/// <summary>
/// Quick Look用の低レベルキーフック。フック内では対象キーのKeyDown判定だけを行い、
/// Explorer/IME/フォーカス調査やファイル読取は呼び出し元へ遅延する。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    private readonly HookProc _callback;
    private readonly int _targetVirtualKey;
    private IntPtr _hook;
    private int _targetKeyDown;
    private bool _disposed;

    /// <param name="targetVirtualKey">
    /// 監視する仮想キーコード。既定はSpace（<see cref="QuickLookShortcutKey.DefaultVirtualKeyCode"/>）。
    /// <see cref="QuickLookShortcutKey"/>で設定文字列から変換した値を渡す。
    /// </param>
    public KeyboardHook(int targetVirtualKey = QuickLookShortcutKey.DefaultVirtualKeyCode)
    {
        _targetVirtualKey = targetVirtualKey;
        _callback = HookCallback;
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, moduleHandle, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("Quick Lookのキーボードフックを開始できませんでした。");
        }
    }

    public event EventHandler? KeyPressed;

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        // 最頻経路を最小化: 対象キーのKeyDown以外はWin32/COM調査を一切せず即座に次へ流す。
        if (code >= 0 && Marshal.ReadInt32(lParam) == _targetVirtualKey)
        {
            if (wParam == (IntPtr)WmKeyUp || wParam == (IntPtr)WmSysKeyUp)
            {
                Interlocked.Exchange(ref _targetKeyDown, 0);
            }
            else if ((wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown) &&
                     Interlocked.Exchange(ref _targetKeyDown, 1) == 0 && !IsModifierDown())
            {
                KeyPressed?.Invoke(this, EventArgs.Empty);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsModifierDown()
        => IsKeyDown(VkShift) || IsKeyDown(VkControl) || IsKeyDown(VkMenu) ||
           IsKeyDown(VkLeftWindows) || IsKeyDown(VkRightWindows);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
