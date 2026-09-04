using System.Text.RegularExpressions;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>
/// Dry Runと実処理で共有するリネーム変数展開。
/// ファイル由来の変数は常に使え、AI由来の変数は解析成功時だけ置換する。
/// </summary>
public static class RenamePatternExpander
{
    /// <summary>
    /// AI解析結果（<see cref="AnalyzeResponse.Metadata"/>）が提供しうる既知のプレースホルダー名。
    /// 解析自体は成功したが該当項目の値が取れなかった場合（例: organization未検出でcompanyが欠落）に、
    /// 生の"{xxx}"をファイル名へ残さず空文字へ確定させる対象として使う。
    /// </summary>
    private static readonly string[] KnownAiTokens = { "category", "date", "company", "document_type", "title" };

    public static string Expand(string pattern, string currentPath, AnalyzeResponse? analysis)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        string expanded = Replace(pattern, "ext", Path.GetExtension(currentPath));
        expanded = Replace(expanded, "filename", Path.GetFileNameWithoutExtension(currentPath));

        if (analysis is null)
        {
            // AI解析未実行（対象ルールにocr_contains/ai_category条件もAI系プレースホルダーも
            // 無い場合）はPhase1と同じくpatternをそのまま使う。{category}等が残っていても、
            // ユーザーが意図的にリテラルの波括弧を使った可能性を否定できないためここでは除去しない。
            return expanded;
        }

        expanded = Replace(expanded, "category", analysis.Category);
        var metadata = analysis.Metadata ?? new Dictionary<string, string>();
        foreach ((string key, string? value) in metadata)
        {
            expanded = Replace(expanded, key, value);
        }

        // よくある誤記への互換: {document} を {document_type} の別名として扱う。
        if (metadata.TryGetValue("document_type", out var documentType))
        {
            expanded = Replace(expanded, "document", documentType);
        }

        // AI解析は行われたが値が無かった項目は、生の"{xxx}"をファイル名に残すと
        // 「2026-09-03_{company}_{document}.pdf」のような意味不明な名前になってしまう
        // （実際に発生した不具合）ため、既知のトークンに限り空文字で確定させる。
        foreach (string token in KnownAiTokens)
        {
            expanded = expanded.Replace("{" + token + "}", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        expanded = expanded.Replace("{document}", string.Empty, StringComparison.OrdinalIgnoreCase);

        // 上記のどれにも当てはまらない"{xxx}"（例: ユーザーが"{title}"を"{titiel}"等と
        // 打ち間違えたトークン名）も、AI解析実行時に限り空文字で確定させる保険。
        // 実際にこの種の入力ミスでファイル名へ生の波括弧が残る不具合が発生したため追加した。
        // 半角英数字・アンダースコアのみで構成される{...}に限定し、ユーザーが意図的に
        // 記号やスペースを含むリテラルな波括弧を使った場合は対象外にする。
        expanded = Regex.Replace(expanded, @"\{[A-Za-z_][A-Za-z0-9_]*\}", string.Empty);

        return CollapseEmptyPlaceholderArtifacts(expanded);
    }

    private static string Replace(string source, string key, string? value)
        => string.IsNullOrEmpty(key) || value is null
            ? source
            : source.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 空値置換で生じた区切り記号の連続・端の余りを整える
    /// （例: "2026-08-25__.pdf" → "2026-08-25.pdf"、"_請求書.pdf" → "請求書.pdf"）。
    /// 最終的な禁止文字・予約名等のサニタイズは<see cref="Utils.PathSanitizer.SanitizeFileName"/>が
    /// 別途必ず行うため、ここでは区切り記号の見た目だけを整える。
    /// </summary>
    private static string CollapseEmptyPlaceholderArtifacts(string value)
    {
        string ext = Path.GetExtension(value);
        string stem = Path.GetFileNameWithoutExtension(value);

        // 同じ区切り文字（_ / -）が連続する箇所を1文字へ圧縮する。
        stem = Regex.Replace(stem, @"([_\-])\1+", "$1");
        stem = stem.Trim('_', '-', ' ');

        return string.IsNullOrEmpty(stem) ? $"renamed{ext}" : $"{stem}{ext}";
    }
}
