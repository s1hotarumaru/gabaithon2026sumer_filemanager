using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Shared.Contracts;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileOrganizer.Infrastructure.Ocr;

/// <summary>
/// SPEC_v3.6.md §3.1「文字認識(OCR)」/ CONTRACTS.md <c>IOcrService</c> の実装。
/// <see cref="PdfToBitmapRenderer"/>（PDF）または<c>Windows.Media.Ocr</c>が直接デコード可能な画像ファイルを
/// 入力として、Windows標準OCR（<c>Windows.Media.Ocr.OcrEngine</c>）でテキストを抽出する。
/// </summary>
/// <remarks>
/// <para>
/// 【プライバシー設計 / 仕様書§7.2-6】
/// <see cref="ExtractTextAsync"/>が返すOCR全文は、このクラス内では一切ファイル・ログへ書き出さず、
/// 呼び出し元へ返す<c>string</c>としてのみ（＝プロセスメモリ上にのみ）存在する。
/// 呼び出し元（例: ProcessingCoordinator）は、この戻り値をPython連携（<c>AnalyzeRequest.OcrText</c>）へ
/// 引き渡した後、DB（<c>HistoryRecord</c>等）やログファイルへ永続化してはならず、
/// リネーム変数生成後は速やかに参照を破棄すること（GCで回収可能な状態にすること）。
/// 本クラス自身も例外時のログ等にOCR全文を含めない。
/// </para>
/// <para>
/// 【言語パック未インストール時のフォールバック】
/// <c>OcrEngine.TryCreateFromUserProfileLanguages()</c>は、ユーザープロファイルの言語設定
/// （＝現在のシステム言語を含む優先順リスト）のうち、OCR言語パックがインストール済みの
/// 言語が1つも無い場合に<c>null</c>を返す。この場合<see cref="IsLanguagePackAvailableAsync"/>は
/// <c>false</c>を、<see cref="ExtractTextAsync"/>は<c>null</c>を返し、例外は投げない。
/// 呼び出し元はこれを受けてルールベース仕分けへgracefulフォールバックすること。
/// </para>
/// </remarks>
public sealed class WindowsMediaOcrService : IOcrService
{
    private readonly PdfToBitmapRenderer _pdfRenderer;

    public WindowsMediaOcrService(PdfToBitmapRenderer? pdfRenderer = null)
    {
        _pdfRenderer = pdfRenderer ?? new PdfToBitmapRenderer();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 現在のシステム言語（ユーザープロファイル言語優先リスト）に対応するOCR言語パックが
    /// 1つもインストールされていない場合に<c>false</c>を返す。
    /// </remarks>
    public Task<bool> IsLanguagePackAvailableAsync()
    {
        // OcrEngineはIClosableを実装しないため明示的な解放は不要（WinRT参照カウントに委ねる）。
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        return Task.FromResult(engine is not null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// PDFは<see cref="PdfToBitmapRenderer"/>で全ページをラスタライズしてから、画像ファイルは
    /// 直接デコードしてから、それぞれ<c>OcrEngine.RecognizeAsync</c>にかけページ順に連結して返す。
    /// 言語パック未インストール・ファイル読み込み失敗・デコード不可・OCR失敗等、想定内の失敗は
    /// 例外を投げず<c>null</c>を返す（呼び出し元でgracefulフォールバック）。
    /// 戻り値の文字列の取り扱いはこのクラスのXMLドキュメント冒頭「プライバシー設計」を参照。
    /// </remarks>
    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // 現在のシステム言語のOCR言語パック未インストールを検出し、gracefulフォールバック（null復帰）。
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        var ownedBitmaps = new List<SoftwareBitmap>();
        try
        {
            bool isPdf = string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<SoftwareBitmap> pageBitmaps;

            if (isPdf)
            {
                // 2-1: PDF各ページを既定200DPIでラスタライズ（PdfToBitmapRenderer側で失敗時は空一覧）。
                pageBitmaps = await _pdfRenderer.RenderAllPagesAsync(filePath, ct: ct).ConfigureAwait(false);
                ownedBitmaps.AddRange(pageBitmaps);
            }
            else
            {
                // 画像ファイル直接の場合: Windows.Media.Ocrが要求するSoftwareBitmapへデコード。
                SoftwareBitmap? single = await LoadImageBitmapAsync(filePath, ct).ConfigureAwait(false);
                if (single is null)
                {
                    return null;
                }

                ownedBitmaps.Add(single);
                pageBitmaps = ownedBitmaps;
            }

            if (pageBitmaps.Count == 0)
            {
                return null;
            }

            string? text = await RecognizeAllPagesAsync(engine, pageBitmaps, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (IsExpectedOcrFailure(ex))
        {
            return null;
        }
        finally
        {
            // OCR全文を返す/返さないに関わらず、中間生成物のビットマップは必ずここで解放する。
            foreach (SoftwareBitmap bitmap in ownedBitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static async Task<string?> RecognizeAllPagesAsync(
        OcrEngine engine, IReadOnlyList<SoftwareBitmap> pageBitmaps, CancellationToken ct)
    {
        // OCR全文はこのStringBuilder（ローカル変数＝メモリ上）にのみ保持し、
        // どこにも書き出さない。呼び出し元へ返した後の破棄責任は呼び出し元にある。
        var fullText = new StringBuilder();

        foreach (SoftwareBitmap bitmap in pageBitmaps)
        {
            ct.ThrowIfCancellationRequested();

            SoftwareBitmap? converted = null;
            SoftwareBitmap ocrInput = bitmap;
            try
            {
                // OcrEngine.RecognizeAsyncが受け付けるピクセル形式（Gray8/Nv12/Bgra8、非プリマルチプライAlpha不可）
                // でない場合はBgra8(Premultiplied)へ変換する。SoftwareBitmap.Convertは常に新規インスタンスを
                // 返すため、変換した場合のみここでDisposeする（元のbitmapの解放責任は呼び出し元にある）。
                bool alreadyCompatible =
                    bitmap.BitmapPixelFormat is BitmapPixelFormat.Gray8 or BitmapPixelFormat.Nv12
                    || (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                        && bitmap.BitmapAlphaMode != BitmapAlphaMode.Straight);

                if (!alreadyCompatible)
                {
                    converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    ocrInput = converted;
                }

                OcrResult result = await engine.RecognizeAsync(ocrInput).AsTask(ct).ConfigureAwait(false);
                string pageText = BuildRecognizedText(result);
                if (!string.IsNullOrEmpty(pageText))
                {
                    if (fullText.Length > 0)
                    {
                        fullText.Append('\n');
                    }
                    fullText.Append(pageText);
                }
            }
            finally
            {
                converted?.Dispose();
            }
        }

        return fullText.ToString();
    }

    /// <summary>
    /// <see cref="OcrResult.Text"/>をそのまま使わず、行・単語から自前で再構成する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>背景（実機で発見した不具合）</b>: <c>Windows.Media.Ocr</c>は日本語の連続漢字/かなを
    /// 単語分割できず、1文字ずつ別々の<see cref="OcrWord"/>として認識する。<see cref="OcrResult.Text"/>
    /// （および<see cref="OcrLine.Text"/>）はこの単語列を単純に半角スペースで連結するため、
    /// 「請求書」が「請 求 書」のように文字間へ余計な空白が入った文字列になってしまう。
    /// この結果、<c>RuleCondition.Type == "ocr_contains"</c>の部分一致判定や、
    /// py_service側<c>RuleBasedClassifier</c>のキーワード完全一致判定（「請求書」「領収書」等）が
    /// 恒久的に不一致となり、AI/ルールベースのカテゴリ分類・リネームが一切機能しなくなる
    /// （実際に発生した不具合: 領収書/請求書PDFがAIカテゴリ条件付きリネームルールに一度も一致せず、
    /// 常に後続のMoveルールへフォールバックしていた）。
    /// </para>
    /// <para>
    /// <b>対処</b>: 単語同士を連結する際、両側の境界文字が共にCJK文字（漢字・ひらがな・カタカナ）の
    /// 場合はスペースを挿入しない。英数字を含む単語同士（型番・金額・URL等）の境界では、
    /// 元々OCRが正しく分かち書きしているため従来通りスペースを維持する。
    /// </para>
    /// </remarks>
    private static string BuildRecognizedText(OcrResult result)
    {
        var sb = new StringBuilder();
        foreach (OcrLine line in result.Lines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            AppendLineWithoutSpuriousCjkSpaces(sb, line);
        }
        return sb.ToString();
    }

    private static void AppendLineWithoutSpuriousCjkSpaces(StringBuilder sb, OcrLine line)
    {
        string? previousWord = null;
        foreach (OcrWord word in line.Words)
        {
            string current = word.Text;
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            if (previousWord is { Length: > 0 } && !(IsCjkChar(previousWord[^1]) && IsCjkChar(current[0])))
            {
                sb.Append(' ');
            }

            sb.Append(current);
            previousWord = current;
        }
    }

    /// <summary>漢字・ひらがな・カタカナ（全角/半角）かどうかを判定する。</summary>
    private static bool IsCjkChar(char c) =>
        (c >= '一' && c <= '鿿')   // CJK統合漢字
        || (c >= '㐀' && c <= '䶿') // CJK統合漢字拡張A
        || (c >= '぀' && c <= 'ゟ') // ひらがな
        || (c >= '゠' && c <= 'ヿ') // カタカナ
        || (c >= 'ｦ' && c <= 'ﾝ'); // 半角カタカナ

    private static async Task<SoftwareBitmap?> LoadImageBitmapAsync(string filePath, CancellationToken ct)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath).AsTask(ct).ConfigureAwait(false);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedOcrFailure(ex))
        {
            // 非対応形式（拡張子は画像だが実体がPDF/破損等）・存在しない・アクセス不可等、
            // 任意のファイルを対象とするOCR前処理として想定内の失敗。
            return null;
        }
    }

    /// <summary>
    /// 破損ファイル・非対応形式・存在しない・アクセス不可・言語パック不整合等、任意のファイルを
    /// 対象とするOCR処理として想定内に扱うべき失敗を判定する。
    /// 想定外の例外（呼び出し側の引数誤り等）はここで握りつぶさず伝播させる。
    /// </summary>
    private static bool IsExpectedOcrFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException;
}
