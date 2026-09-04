using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Core.Utils;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Engine;

/// <summary>1回の<see cref="ProcessingCoordinator.ProcessAsync"/>呼び出しが完了した際の結果通知（観測・ログ用）。</summary>
public sealed class ProcessingCompletedEventArgs : EventArgs
{
    public string SourceFullPath { get; }
    public IReadOnlyList<HistoryRecord> Records { get; }

    public ProcessingCompletedEventArgs(string sourceFullPath, IReadOnlyList<HistoryRecord> records)
    {
        SourceFullPath = sourceFullPath;
        Records = records;
    }
}

/// <summary>
/// 仕様書§3.3「実行時フロー」（実行前にDBへ<c>Planned</c>を保存 → 操作直前に<c>Executing</c> →
/// 操作成功確認後に<c>Completed</c>へ更新。失敗時は<c>Failed</c>）を実装するパイプライン統合クラス。
/// </summary>
/// <remarks>
/// <para>
/// <b>パイプライン</b>: 1-5 <see cref="FileStabilityDetector"/>の安定通知（<see cref="FileStableEventArgs"/>）
/// を受け取り → 1-7 <see cref="IRuleEngine"/>（<see cref="RuleEvaluator"/>）で評価 →
/// 一致した各ルールの各<see cref="RuleAction"/>について、1-3 <see cref="IHistoryRepository"/>
/// （<see cref="Database.SqliteHistoryRepository"/>）へ<c>Planned</c>状態で事前記録 →
/// <c>Executing</c>へ更新 → 1-8 <see cref="IFileOperationService"/>
/// （<see cref="Services.FileOperationService"/>）で実操作を実行 →
/// 結果に応じて<c>Completed</c>/<c>Failed</c>へ更新、という一連の流れを担う。
/// </para>
/// <para>
/// <b>複数ルール一致時</b>: <c>AppSettings.ApplyAllMatchingRules</c>が<c>false</c>（既定）なら
/// <see cref="RuleEvaluationResult.MatchedRule"/>（優先順位最上位1件）のみ、<c>true</c>なら
/// <see cref="RuleEvaluationResult.AllMatchedRules"/>（優先順位順）すべてを対象にする
/// （評価順序自体は<see cref="RuleEvaluator"/>が仕様書§6に従って決定済み）。
/// </para>
/// <para>
/// <b>1ルール内の複数アクション</b>: <see cref="RuleModel.Actions"/>は順に実行する。
/// 現在の対象パス（<c>currentPath</c>）はMove/Renameで移動先へ更新され、以降のアクション・ルールは
/// その新しいパスに対して実行される（Copyは元ファイルを維持するため対象パスを変えない）。
/// いずれかのアクションが失敗（<see cref="OperationResult.Success"/>=false）した場合、
/// ファイルの所在が不確実になるため、後続のアクション・ルールは実行せずそこで打ち切る。
/// 同名衝突による意図的なスキップ（<see cref="ConflictPolicy.Skip"/>）は失敗ではないため、
/// 元のパスのまま後続アクションへ継続する。
/// </para>
/// <para>
/// <b>本文抽出/AI連携（Phase2）</b>: 対象ルール群（<c>settings.LoadRulesAsync</c>で読み込んだ全ルール）の
/// いずれかが<c>ocr_contains</c>または<c>ai_category</c>条件を持つ場合に限り、ルール評価（<see cref="RuleEvaluator"/>）
/// の前に<see cref="EnrichWithAiMetadataAsync"/>で
/// TXT/DOCXは本文を直接読み込み、PDF/画像は<c>WindowsMediaOcrService</c>でOCRして、
/// 0-4 <see cref="IPythonApiClient.AnalyzeAsync"/>でカテゴリ・メタデータを取得する。
/// 取得した<see cref="FileMetadata.OcrText"/>/<see cref="FileMetadata.AiCategory"/>を反映した上で
/// <see cref="RuleEvaluator"/>を評価するため、これらの条件を持つルールも正しく判定できる。
/// 該当条件を持つルールが1件も無い場合は本文抽出/HTTP呼び出しのコストを避けるため一切呼び出さない
/// （Phase1と同じ挙動）。
/// </para>
/// <para>
/// <b>フォールバック（仕様書§3.1「コンテンツ解析自動リネーム」）</b>: OCR言語パック未インストール・
/// 本文抽出失敗・Python API呼び出し失敗は、いずれも例外を投げず該当ステップのみをスキップして
/// ルールベース仕分けへgracefulに退避する（<see cref="EnrichWithAiMetadataAsync"/>参照）。
/// </para>
/// <para>
/// <b>プライバシー（仕様書§7.2-6）</b>: 抽出テキスト全文は<see cref="FileMetadata.OcrText"/>
/// （呼び出し元が用意したローカルインスタンス）とPython連携用の一時リクエストにのみ乗り、
/// <see cref="HistoryRecord"/>を含むDB書き込み・ログ出力の経路には一切渡さない。
/// </para>
/// </remarks>
public sealed class ProcessingCoordinator
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IHistoryRepository _historyRepository;
    private readonly IFileOperationService _fileOperationService;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IContentTextExtractor? _textExtractor;
    private readonly IPythonApiClient? _pythonApiClient;
    private readonly ConflictPolicy _defaultConflictPolicy;

    /// <summary>1件の安定ファイルに対する処理が完了するたびに発火する（観測・ログ用、任意購読）。</summary>
    public event EventHandler<ProcessingCompletedEventArgs>? ProcessingCompleted;

    /// <param name="ruleEngine">1-7 ルール評価エンジン（<see cref="RuleEvaluator"/>）。</param>
    /// <param name="historyRepository">1-3 2フェーズ状態管理リポジトリ（<see cref="Database.SqliteHistoryRepository"/>）。</param>
    /// <param name="fileOperationService">1-8 実ファイル操作サービス（<see cref="Services.FileOperationService"/>）。</param>
    /// <param name="settingsRepository">ルール一覧・<c>ApplyAllMatchingRules</c>設定の取得元。</param>
    /// <param name="ocrService">
    /// 従来のOCR抽出の呼び出し口。<c>null</c>の場合、
    /// <c>ocr_contains</c>/<c>ai_category</c>ルールが存在してもAI解析は行わずルールベースのみで動作する。
    /// </param>
    /// <param name="pythonApiClient">
    /// 0-4 AI/SLM解析の呼び出し口（<c>PythonApiClient</c>）。<c>null</c>の場合は上記同様AI解析を行わない。
    /// </param>
    /// <param name="defaultConflictPolicy">
    /// Move/Copyアクション実行時の同名衝突ポリシー（<c>RuleAction</c>自体は保持しないため、
    /// <c>AppSettings</c>相当のサービス既定値として渡す）。既定は<see cref="ConflictPolicy.AutoRename"/>。
    /// </param>
    public ProcessingCoordinator(
        IRuleEngine ruleEngine,
        IHistoryRepository historyRepository,
        IFileOperationService fileOperationService,
        ISettingsRepository settingsRepository,
        IOcrService? ocrService = null,
        IPythonApiClient? pythonApiClient = null,
        ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename)
        : this(
            ruleEngine,
            historyRepository,
            fileOperationService,
            settingsRepository,
            (IContentTextExtractor?)ocrService,
            pythonApiClient,
            defaultConflictPolicy)
    {
    }

    /// <summary>TXT/DOCX直接読み込みを含む汎用本文抽出器を使うコンストラクタ。</summary>
    public ProcessingCoordinator(
        IRuleEngine ruleEngine,
        IHistoryRepository historyRepository,
        IFileOperationService fileOperationService,
        ISettingsRepository settingsRepository,
        IContentTextExtractor? textExtractor,
        IPythonApiClient? pythonApiClient = null,
        ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
        _fileOperationService = fileOperationService ?? throw new ArgumentNullException(nameof(fileOperationService));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _textExtractor = textExtractor;
        _pythonApiClient = pythonApiClient;
        _defaultConflictPolicy = defaultConflictPolicy;
    }

    /// <summary>
    /// <see cref="FileStabilityDetector.FileStabilized"/>イベントへ本コーディネーターを接続する
    /// （1-5との実配線）。
    /// </summary>
    public void AttachTo(FileStabilityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        detector.FileStabilized += OnFileStabilized;
    }

    public void Detach(FileStabilityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        detector.FileStabilized -= OnFileStabilized;
    }

    private async void OnFileStabilized(object? sender, FileStableEventArgs e)
    {
        try
        {
            await ProcessAsync(e.Metadata).ConfigureAwait(false);
        }
        catch
        {
            // イベントハンドラから例外を漏らさない
            // （FileStabilityDetectorの単一集約ワーカーループを止めないため）。
        }
    }

    /// <summary>
    /// 安定確認済みの1ファイルに対し、ルール評価からファイル操作・履歴更新までの
    /// 一連のパイプラインを実行する。<see cref="FileStabilityDetector"/>からのイベント経由に限らず、
    /// 直接呼び出すこともできる（テスト・手動実行・定期走査からの再投入にも利用可能）。
    /// </summary>
    /// <returns>実行した各ファイル操作の最終状態の<see cref="HistoryRecord"/>一覧（一致ルールが無ければ空）。</returns>
    public async Task<IReadOnlyList<HistoryRecord>> ProcessAsync(FileMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!File.Exists(metadata.FullPath))
        {
            // 安定検知後、処理開始までの間にユーザー操作等で消失した場合は何もしない。
            var empty = Array.Empty<HistoryRecord>();
            ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, empty));
            return empty;
        }

        AppSettings settings = await _settingsRepository.LoadSettingsAsync(ct).ConfigureAwait(false);
        List<RuleModel> rules = await _settingsRepository.LoadRulesAsync(ct).ConfigureAwait(false);

        // Phase2: ocr_contains/ai_category条件を持つ有効ルールが1件でもある場合のみ、
        // 本文抽出→AI解析パイプラインを実行してmetadata.OcrText/AiCategoryを埋める。
        // 対象ルールが無ければ抽出・HTTP通信のコストを避けるため一切呼び出さない（Phase1同様の挙動）。
        AnalyzeResponse? analyzeResponse = RequiresAiEnrichment(rules)
            ? await EnrichWithAiMetadataAsync(metadata, ct).ConfigureAwait(false)
            : null;

        RuleEvaluationResult evaluation = _ruleEngine.Evaluate(metadata, rules, settings.ApplyAllMatchingRules);
        if (!evaluation.IsMatched)
        {
            var empty = Array.Empty<HistoryRecord>();
            ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, empty));
            return empty;
        }

        IReadOnlyList<RuleModel> rulesToApply = settings.ApplyAllMatchingRules
            ? evaluation.AllMatchedRules
            : new List<RuleModel> { evaluation.MatchedRule! };

        var records = new List<HistoryRecord>();
        string currentPath = metadata.FullPath;

        foreach (var rule in rulesToApply)
        {
            foreach (var action in rule.Actions)
            {
                ct.ThrowIfCancellationRequested();

                ActionOutcome outcome = await ExecuteActionAsync(currentPath, action, analyzeResponse, ct).ConfigureAwait(false);
                if (outcome.Record != null)
                {
                    records.Add(outcome.Record);
                }
                currentPath = outcome.NextPath;

                if (outcome.StopChain)
                {
                    ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, records));
                    return records;
                }
            }
        }

        ProcessingCompleted?.Invoke(this, new ProcessingCompletedEventArgs(metadata.FullPath, records));
        return records;
    }

    // --- 1アクション分の Planned→Executing→Completed/Failed 実行 ---------------------------

    private readonly record struct ActionOutcome(HistoryRecord? Record, string NextPath, bool StopChain);

    private async Task<ActionOutcome> ExecuteActionAsync(string currentPath, RuleAction action, AnalyzeResponse? analyzeResponse, CancellationToken ct)
    {
        if (!TryMapOperationType(action.Type, out OperationType opType))
        {
            // 未知のaction typeは安全側で無視し、後続へ継続する。
            return new ActionOutcome(null, currentPath, StopChain: false);
        }

        if (!File.Exists(currentPath))
        {
            // 前段のアクション（Recycle等）で既に対象が消失している → これ以上進められない。
            return new ActionOutcome(null, currentPath, StopChain: true);
        }

        var fileInfo = new FileInfo(currentPath);
        string lightweightHash = HashHelper.ComputeLightweightHash(currentPath);

        var record = new HistoryRecord
        {
            OpType = opType,
            SourcePath = currentPath,
            // DestinationPath（Planned時点の見込み）にもAI解析結果によるプレースホルダー展開を反映する。
            // HistoryRecordにOCR全文自体は含まれない点に注意（仕様書§7.2-6）。
            DestinationPath = BuildIntendedDestinationPath(currentPath, action, opType, analyzeResponse),
            FileSizeBytes = fileInfo.Length,
            FileLastModifiedUtc = fileInfo.LastWriteTimeUtc,
            LightweightHash = lightweightHash,
            State = OperationState.Planned,
        };

        // 1-3: 操作実行前にPlannedとしてDBへ事前記録。
        long id = await _historyRepository.InsertAsync(record, ct).ConfigureAwait(false);
        record.Id = id;

        await _historyRepository.UpdateStateAsync(id, OperationState.Executing, ct: ct).ConfigureAwait(false);
        record.State = OperationState.Executing;

        OperationResult opResult;
        try
        {
            // 1-8: 実ファイル操作。
            opResult = await ExecuteFileOperationAsync(currentPath, action, opType, analyzeResponse, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _historyRepository.UpdateStateAsync(id, OperationState.Failed, ex.Message, ct).ConfigureAwait(false);
            record.State = OperationState.Failed;
            record.ErrorMessage = ex.Message;
            return new ActionOutcome(record, currentPath, StopChain: true);
        }

        if (opResult.Success && !opResult.WasSkippedDueToConflict)
        {
            await _historyRepository.UpdateStateAsync(id, OperationState.Completed, ct: ct).ConfigureAwait(false);
            record.State = OperationState.Completed;
            record.DestinationPath = opResult.FinalPath ?? record.DestinationPath;

            // Move/Renameは対象ファイルの所在自体が変わるため、以降はこの新しいパスを対象とする。
            // Copyは元ファイルを維持するため対象パスは変えない。
            string nextPath = opType is OperationType.Move or OperationType.Rename
                ? (opResult.FinalPath ?? currentPath)
                : currentPath;

            return new ActionOutcome(record, nextPath, StopChain: false);
        }

        if (opResult.Success && opResult.WasSkippedDueToConflict)
        {
            // ConflictPolicy.Skipによる意図的な無処理。失敗ではないため後続へ継続する
            // （対象ファイルは元のパスのまま）。
            const string skipMessage = "同名衝突のためスキップされました（Skipポリシー）。";
            await _historyRepository.UpdateStateAsync(id, OperationState.Completed, skipMessage, ct).ConfigureAwait(false);
            record.State = OperationState.Completed;
            record.ErrorMessage = skipMessage;
            record.DestinationPath = null;
            return new ActionOutcome(record, currentPath, StopChain: false);
        }

        // 失敗（ConflictPolicy.PromptUserによる要確認を含む）。ファイルの所在が不確実になるため中断する。
        string errorMessage = opResult.ErrorMessage ?? "不明なエラーにより操作に失敗しました。";
        await _historyRepository.UpdateStateAsync(id, OperationState.Failed, errorMessage, ct).ConfigureAwait(false);
        record.State = OperationState.Failed;
        record.ErrorMessage = errorMessage;
        return new ActionOutcome(record, currentPath, StopChain: true);
    }

    private Task<OperationResult> ExecuteFileOperationAsync(
        string currentPath, RuleAction action, OperationType opType, AnalyzeResponse? analyzeResponse, CancellationToken ct)
    {
        return opType switch
        {
            OperationType.Move => _fileOperationService.MoveAsync(currentPath, RequireDestination(action), _defaultConflictPolicy, ct),
            OperationType.Copy => _fileOperationService.CopyAsync(currentPath, RequireDestination(action), _defaultConflictPolicy, ct),
            // AI解析結果（category/metadata）とファイル由来変数によるプレースホルダー展開を
            // 適用した上で新ファイル名として使用する。analyzeResponseがnull（AI解析未実行/失敗）の場合は
            // Phase1と同じくPatternをそのまま使用する。禁止文字・末尾ドット等のサニタイズは
            // FileOperationService.RenameAsync側で必ず行われる。
            OperationType.Rename => _fileOperationService.RenameAsync(
                currentPath, RenamePatternExpander.Expand(RequirePattern(action), currentPath, analyzeResponse), ct),
            OperationType.Recycle => _fileOperationService.RecycleAsync(currentPath, ct),
            _ => throw new InvalidOperationException($"未対応のOperationTypeです: {opType}"),
        };
    }

    private static string RequireDestination(RuleAction action)
        => !string.IsNullOrWhiteSpace(action.Destination)
            ? action.Destination
            : throw new InvalidOperationException($"アクション種別'{action.Type}'にはdestinationの指定が必須です。");

    private static string RequirePattern(RuleAction action)
        => !string.IsNullOrWhiteSpace(action.Pattern)
            ? action.Pattern
            : throw new InvalidOperationException("rename アクションにはpatternの指定が必須です。");

    private static string? BuildIntendedDestinationPath(
        string currentPath, RuleAction action, OperationType opType, AnalyzeResponse? analyzeResponse) => opType switch
    {
        OperationType.Move or OperationType.Copy => !string.IsNullOrWhiteSpace(action.Destination)
            ? Path.Combine(action.Destination, Path.GetFileName(currentPath))
            : null,
        OperationType.Rename => !string.IsNullOrWhiteSpace(action.Pattern)
            ? Path.Combine(Path.GetDirectoryName(currentPath) ?? string.Empty, RenamePatternExpander.Expand(action.Pattern, currentPath, analyzeResponse))
            : null,
        _ => null, // Recycleに移動先の概念はない
    };

    private static bool TryMapOperationType(string actionType, out OperationType opType)
    {
        switch (actionType?.Trim().ToLowerInvariant())
        {
            case "move": opType = OperationType.Move; return true;
            case "copy": opType = OperationType.Copy; return true;
            case "rename": opType = OperationType.Rename; return true;
            case "recycle": opType = OperationType.Recycle; return true;
            default: opType = default; return false;
        }
    }

    /// <summary>
    /// AI_IMPLEMENTATION_GUIDE.md §3.2の<c>POST /api/v1/analyze</c>リクエスト例に合わせた既定の
    /// <c>extract_fields</c>。ルール側にフィールド一覧を指定する仕組みが無いため固定値を使用する。
    /// <c>title</c>は<c>{date}_{company}_{document_type}</c>相当を、py_service側（<c>naming.suggest_base_name</c>）が
    /// 組織名/日付未検出時のフォールバック（「発行元不明」「日付不明」）まで含めて1トークンで
    /// 組み立て済みの値として提供する。個別トークンを並べる代わりに<c>{title}</c>だけを使う
    /// パターンなら、値が欠けても生の"{xxx}"が残る心配がない。
    /// </summary>
    private static readonly List<string> DefaultExtractFields = new() { "date", "company", "document_type", "category", "title" };

    /// <summary><paramref name="rules"/>に、OCR/AI解析を必要とする条件を持つ有効ルールが1件でもあるかを判定する。</summary>
    private static bool RequiresAiEnrichment(IReadOnlyList<RuleModel> rules)
        => rules.Any(rule => rule.Enabled &&
            (rule.Conditions.Any(condition => condition.Type is "ocr_contains" or "ai_category") ||
             rule.Actions.Any(action => action.Type.Equals("rename", StringComparison.OrdinalIgnoreCase) &&
                 (action.Pattern?.Contains("{category}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{date}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{company}", StringComparison.OrdinalIgnoreCase) == true ||
                  action.Pattern?.Contains("{document_type}", StringComparison.OrdinalIgnoreCase) == true))));

    /// <summary>
    /// Phase2 本文抽出/AI解析パイプライン: TXT/DOCXは直接読み込み、PDF/画像はOCRして本文を抽出し、
    /// 0-4 <see cref="IPythonApiClient.AnalyzeAsync"/>で<c>category</c>/<c>metadata</c>を取得する。
    /// 成功した範囲までの結果を<paramref name="metadata"/>（<see cref="FileMetadata.OcrText"/>/
    /// <see cref="FileMetadata.AiCategory"/>）へ反映した上で、リネームpatternの変数展開に使う
    /// <see cref="AnalyzeResponse"/>を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>フォールバック</b>: 以下はいずれも例外を投げず、当該ステップのみをスキップして
    /// ルールベース仕分けへgracefulに退避する（仕様書§3.1「コンテンツ解析自動リネーム」）。
    /// <list type="bullet">
    /// <item>本文抽出未構成（<see cref="_textExtractor"/>/<see cref="_pythonApiClient"/>が<c>null</c>）</item>
    /// <item>OCR対象で現在のシステム言語パックが利用できない場合</item>
    /// <item>本文抽出失敗（<see cref="IContentTextExtractor.ExtractTextAsync"/>が<c>null</c>を返す、または想定外の例外を投げた場合）
    /// → この場合Python連携自体を呼び出さない（テキストが無い解析は意味を持たないため）。
    /// ただし<c>ocr_contains</c>条件は抽出本文そのもので判定するため、本文抽出が成功していれば
    /// Python解析の成否に関わらず<see cref="FileMetadata.OcrText"/>は設定する。</item>
    /// <item>Python API呼び出し失敗（<see cref="IPythonApiClient.AnalyzeAsync"/>が<c>null</c>を返す・
    /// 例外を投げる・<see cref="AnalyzeResponse.Success"/>が<c>false</c>）→ <c>ai_category</c>は
    /// 未設定のまま（該当ルールは不一致として扱われる）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>プライバシー（仕様書§7.2-6）</b>: 抽出本文はここで抽出したローカル変数と、Python連携用の
    /// 一時的な<see cref="AnalyzeRequest"/>にのみ乗る。DB（<see cref="HistoryRecord"/>）・ログへは
    /// 一切書き込まず、Python連携（<see cref="IPythonApiClient.AnalyzeAsync"/>）へ引き渡した直後に
    /// ローカル参照を破棄する。戻り値の<see cref="AnalyzeResponse"/>自体には抽出本文を含まない
    /// （<c>category</c>/<c>metadata</c>/<c>confidence</c>のみ）。
    /// </para>
    /// </remarks>
    private async Task<AnalyzeResponse?> EnrichWithAiMetadataAsync(FileMetadata metadata, CancellationToken ct)
    {
        if (_textExtractor is null)
        {
            // 本文抽出依存先が未構成 → ファイル名等の基本ルールへ委ねる。
            return null;
        }

        string? contentText;
        try
        {
            // IOcrServiceを直接注入する既存構成との互換性を維持する。
            // ContentTextExtractionRouterではTXT/DOCX経路にこの確認を適用しない。
            if (_textExtractor is IOcrService ocrService &&
                !await ocrService.IsLanguagePackAvailableAsync().ConfigureAwait(false))
            {
                return null;
            }

            contentText = await _textExtractor.ExtractTextAsync(metadata.FullPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 本文抽出実装は原則例外を投げずnullを返す設計だが、予期しない実装の
            // 誤りも含め安全側でフォールバックする（本パイプライン全体を止めない）。
            contentText = null;
        }

        if (string.IsNullOrWhiteSpace(contentText))
        {
            // 本文抽出失敗（または対象外ファイル）→ Python連携も行わずgracefulに退避。
            // ocr_contains/ai_category条件は未設定のまま（RuleEvaluatorは常に不一致として扱う）。
            return null;
        }

        // ocr_contains条件は抽出本文への一致判定のため、Python解析の成否に関わらずここで設定する。
        metadata.OcrText = contentText;

        if (_pythonApiClient is null)
        {
            // Pythonが起動できない環境でもC#本文抽出だけでocr_contains条件は利用できる。
            return null;
        }

        var request = new AnalyzeRequest
        {
            FilePath = metadata.FullPath,
            OcrText = contentText.Length <= AnalyzeRequest.MaxOcrTextLength
                ? contentText
                : contentText[..AnalyzeRequest.MaxOcrTextLength],
            ExtractFields = DefaultExtractFields,
        };

        AnalyzeResponse? response;
        try
        {
            // 0-4 PythonApiClient.AnalyzeAsync呼び出し。
            response = await _pythonApiClient.AnalyzeAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 未Configure（Python未起動/ハンドシェイク未了）等、IPythonApiClient実装側が例外化しうる
            // 呼び出し側の誤りも含め、ここでは握りつぶしgracefulにフォールバックする。
            response = null;
        }
        finally
        {
            // 抽出本文はPython連携へ引き渡した時点で役目を終える。このメソッド内のローカル参照を
            // 明示的に手放す（仕様書§7.2-6: メモリからの速やかな破棄。DB/ログへは元々渡していない）。
            // ※requestオブジェクト自体は書き換えない（呼び出し元/テストが送信内容を検証できるように、
            //   また既にAnalyzeAsync呼び出しが完了済みの短命ローカルDTOを事後変更する必要は無いため）。
            contentText = null;
        }

        if (response is null || !response.Success)
        {
            // ai_category条件は未設定のまま（gracefulフォールバック）。ocr_containsは上で設定済みのため有効。
            return null;
        }

        // ai_category条件をこの後のRuleEvaluator評価で使えるようにする。
        metadata.AiCategory = response.Category;
        return response;
    }

    /// <summary>
    /// リネームpattern中の<c>{category}</c>・<c>{&lt;metadataキー&gt;}</c>
    /// （例: <c>{date}</c>, <c>{company}</c>, <c>{document_type}</c>）を、
    /// 0-4 <see cref="IPythonApiClient.AnalyzeAsync"/>のレスポンス（<see cref="AnalyzeResponse.Category"/> /
    /// <see cref="AnalyzeResponse.Metadata"/>）の値で置換する。
    /// <paramref name="analyzeResponse"/>が<c>null</c>（AI解析未実行・失敗）の場合はPhase1と同じく
    /// <paramref name="pattern"/>をそのまま返す（プレースホルダーは展開されない）。
    /// 展開後の禁止文字・末尾ドット等のサニタイズは<see cref="Services.FileOperationService.RenameAsync"/>
    /// 側で必ず行われるため、ここでは行わない。
    /// </summary>
}
