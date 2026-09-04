namespace FileOrganizer.Widgets.QuickLook;

/// <summary>
/// 設定画面で入力されたショートカット文字列（例: "Space", "F", "F2", "Insert"）を
/// <see cref="KeyboardHook"/>が使う仮想キーコード（WH_KEYBOARD_LL用）へ変換する。
/// UI（入力検証）とWidgets（実際のフック登録）の両方から同じ変換結果を使うための単一実装。
/// </summary>
public static class QuickLookShortcutKey
{
    /// <summary>既定のショートカット表記。</summary>
    public const string DefaultShortcut = "Space";

    /// <summary>既定の仮想キーコード（VK_SPACE）。</summary>
    public const int DefaultVirtualKeyCode = 0x20;

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Insert"] = 0x2D,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
    };

    /// <summary>
    /// ショートカット文字列を仮想キーコードへ変換する。対応形式は名前付きキー（上記一覧）と、
    /// 英字1文字（A〜Z）・数字1文字（0〜9）。解釈できない場合はfalseを返す（例外は投げない）。
    /// </summary>
    /// <param name="shortcut">設定画面等から入力された文字列。</param>
    /// <param name="virtualKeyCode">解決できた場合の仮想キーコード。</param>
    /// <param name="normalizedLabel">解決できた場合の表示用正規化表記（例: "space" → "Space"）。</param>
    public static bool TryParse(string? shortcut, out int virtualKeyCode, out string normalizedLabel)
    {
        virtualKeyCode = 0;
        normalizedLabel = string.Empty;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;

        string trimmed = shortcut.Trim();

        foreach (var (name, code) in NamedKeys)
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                virtualKeyCode = code;
                normalizedLabel = name;
                return true;
            }
        }

        if (trimmed.Length == 1)
        {
            char c = char.ToUpperInvariant(trimmed[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                // VK_A〜VK_Z・VK_0〜VK_9は対応するASCIIコードと同一値。
                virtualKeyCode = c;
                normalizedLabel = c.ToString();
                return true;
            }
        }

        return false;
    }
}
