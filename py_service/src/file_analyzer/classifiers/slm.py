from __future__ import annotations

import json
import re
from datetime import date
from pathlib import Path
from threading import Lock
from typing import Any

from ..errors import InvalidModelOutput, SlmUnavailable
from ..models import DOCUMENT_TYPES, ClassificationCandidate

_EXPECTED_KEYS = {"document_type", "organization", "document_date", "confidence", "reason"}

# JSON Schema for grammar-constrained decoding (llama-cpp-python builds a GBNF
# grammar from this via response_format={"type": "json_object", "schema": ...}).
# This forces the model's raw token output to already satisfy document_type's
# enum, the date pattern, and the required/closed key set, instead of relying
# on parse_slm_json() to reject bad output after the fact and fall back to the
# (less accurate) rule-based classifier. Ranges (confidence 0-1) and semantic
# validity (e.g. a real calendar date) still need parse_slm_json()'s checks,
# since grammar constraints are syntactic only.
_RESPONSE_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "document_type": {"type": "string", "enum": list(DOCUMENT_TYPES)},
        "organization": {
            "anyOf": [{"type": "string", "maxLength": 100}, {"type": "null"}]
        },
        "document_date": {
            "anyOf": [
                {"type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$"},
                {"type": "null"},
            ]
        },
        "confidence": {"anyOf": [{"type": "number"}, {"type": "null"}]},
        "reason": {"anyOf": [{"type": "string", "maxLength": 200}, {"type": "null"}]},
    },
    "required": ["document_type", "organization", "document_date", "confidence", "reason"],
    "additionalProperties": False,
}


def _first_json_object(value: str) -> dict[str, Any]:
    decoder = json.JSONDecoder()
    for match in re.finditer(r"\{", value):
        try:
            result, _ = decoder.raw_decode(value[match.start() :])
        except json.JSONDecodeError:
            continue
        if isinstance(result, dict):
            return result
    raise InvalidModelOutput("SLM応答にJSONオブジェクトがありません")


def parse_slm_json(value: str) -> ClassificationCandidate:
    if len(value) > 8_000:
        raise InvalidModelOutput("SLM応答が長すぎます")
    data = _first_json_object(value)
    missing = _EXPECTED_KEYS - set(data)
    if missing:
        raise InvalidModelOutput(f"SLM応答に必須フィールドがありません: {sorted(missing)}")
    unknown = set(data) - _EXPECTED_KEYS
    if unknown:
        raise InvalidModelOutput(f"SLM応答に未定義フィールドがあります: {sorted(unknown)}")

    document_type = data.get("document_type")
    if document_type not in DOCUMENT_TYPES:
        raise InvalidModelOutput("SLM応答の文書種別が不正です")

    organization = data.get("organization")
    if organization is not None:
        if not isinstance(organization, str) or len(organization) > 100 or any(ord(c) < 32 for c in organization):
            organization = None
        else:
            organization = organization.strip() or None

    document_date = data.get("document_date")
    if document_date is not None:
        if not isinstance(document_date, str):
            document_date = None
        else:
            try:
                document_date = date.fromisoformat(document_date).isoformat()
            except ValueError:
                document_date = None

    confidence = data.get("confidence")
    if confidence is not None:
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not 0 <= confidence <= 1:
            confidence = None
        else:
            confidence = float(confidence)

    reason = data.get("reason")
    if reason is not None and (not isinstance(reason, str) or len(reason) > 200):
        reason = None

    return ClassificationCandidate(
        document_type=document_type,
        organization=organization,
        document_date=document_date,
        confidence=confidence,
        reason=reason,
        source="slm",
    )


class LlamaCppSlmClassifier:
    """Embedded GGUF classifier backed by llama-cpp-python."""

    def __init__(
        self,
        model_path: Path | None,
        *,
        context_size: int = 4_096,
        threads: int | None = None,
        max_tokens: int = 384,
        input_chars: int = 4_000,
        unload_after_inference: bool = False,
    ) -> None:
        self.model_path = model_path
        self.context_size = context_size
        self.threads = threads
        self.max_tokens = max_tokens
        self.input_chars = input_chars
        self.unload_after_inference = unload_after_inference
        self._model: Any | None = None
        self._lock = Lock()

    @property
    def available(self) -> bool:
        if self.model_path is None or not self.model_path.is_file():
            return False
        try:
            import llama_cpp  # noqa: F401
        except ImportError:
            return False
        return True

    @property
    def model_name(self) -> str | None:
        return self.model_path.name if self.model_path else None

    def _load(self) -> Any:
        if self._model is not None:
            return self._model
        if self.model_path is None or not self.model_path.is_file():
            raise SlmUnavailable("Gemma GGUFモデルが設定されていません")
        try:
            from llama_cpp import Llama
        except ImportError as exc:
            raise SlmUnavailable("llama-cpp-pythonがインストールされていません") from exc
        options: dict[str, Any] = {
            "model_path": str(self.model_path),
            "n_ctx": self.context_size,
            "chat_format": "gemma",
            "verbose": False,
        }
        if self.threads is not None:
            options["n_threads"] = self.threads
        self._model = Llama(**options)
        return self._model

    def close(self) -> None:
        model, self._model = self._model, None
        if model is not None and callable(getattr(model, "close", None)):
            model.close()

    def classify(
        self,
        text: str,
        original_file_name: str,
        baseline: ClassificationCandidate,
    ) -> ClassificationCandidate:
        prompt = self._prompt(text, original_file_name, baseline)
        with self._lock:
            try:
                response = self._load().create_chat_completion(
                    messages=[{"role": "user", "content": prompt}],
                    temperature=0.1,
                    max_tokens=self.max_tokens,
                    response_format={"type": "json_object", "schema": _RESPONSE_SCHEMA},
                )
                content = response["choices"][0]["message"]["content"]
                if not isinstance(content, str):
                    raise InvalidModelOutput("SLM応答本文がありません")
                return parse_slm_json(content)
            except (KeyError, IndexError, TypeError) as exc:
                raise InvalidModelOutput("SLM応答形式が不正です") from exc
            except (InvalidModelOutput, SlmUnavailable):
                raise
            except Exception as exc:
                raise SlmUnavailable("SLM推論に失敗しました") from exc
            finally:
                if self.unload_after_inference:
                    self.close()

    def warmup(self) -> None:
        baseline = ClassificationCandidate(
            document_type="invoice",
            organization="サンプル株式会社",
            document_date="2026-08-31",
        )
        self.classify(
            "請求書\n発行元: サンプル株式会社\n請求日: 2026年8月31日\n請求金額: 3,980円",
            "warmup_invoice.txt",
            baseline,
        )

    def _prompt(
        self,
        text: str,
        original_file_name: str,
        baseline: ClassificationCandidate,
    ) -> str:
        baseline_json = json.dumps(baseline.public_dict(), ensure_ascii=False)
        return f"""あなたは文書分類器です。説明文やMarkdownを付けずJSONオブジェクトだけを返してください。
次の文書を分類してください。
許可するdocument_type: {json.dumps(DOCUMENT_TYPES, ensure_ascii=False)}
- receipt(領収書): その場で支払い済みの証明。「領収書」「お預り」「お釣り」等。
- invoice(請求書): これから支払う請求。「請求書」「お支払期限」「振込先」等。
- meeting_minutes(議事録): 会議の記録。「議事録」「出席者」「決定事項」等。
- contract(契約書): 当事者間の合意文書。「契約書」「甲」「乙」「契約期間」等。
- lecture_material(講義資料): 大学等の講義・授業で配布される資料。「講義資料」「レジュメ」「シラバス」「第◯回」「履修」等。
- specification(仕様書): システム・製品等の仕様や要件を定めた文書。「仕様書」「要件定義」「設計書」等。
- 上記のどれにも明確に該当しない場合のみ other。

organizationは、この文書を発行した側（発行元・請求元・差出人）の組織名にしてください。
講義資料の場合は開講大学・学部や担当教員名など、この資料を作成・配布した側を指します。
宛先・請求先・「様」「御中」で呼ばれる相手側の組織名は使わないでください。
document_dateは、この文書自体の発行日・作成日にしてください。
支払期限・有効期限・契約期間の開始日など、発行日以外の日付と取り違えないでください。
document_dateは実在する日付のYYYY-MM-DDまたはnullにしてください。不明な日付を空文字や「不明」で返さずnullにしてください。
organizationは100文字以下またはnull、confidenceは0から1またはnull、reasonは200文字以下またはnullにしてください。
出力キーは document_type, organization, document_date, confidence, reason の5個だけです。

規則ベース候補はキーワード一致による粗い推定であり、参考情報に過ぎません。
<document>の本文の内容が規則ベース候補と食い違う場合は、本文の内容を優先してください。

元ファイル名: {Path(original_file_name).name}
規則ベース候補（参考、誤っている場合あり）: {baseline_json}
<document>
{text[: self.input_chars]}
</document>"""
