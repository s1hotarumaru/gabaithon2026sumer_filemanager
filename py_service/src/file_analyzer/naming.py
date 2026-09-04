from __future__ import annotations

import re

from .models import FinalDecision

DOCUMENT_TYPE_LABELS = {
    "receipt": "領収書",
    "invoice": "請求書",
    "meeting_minutes": "議事録",
    "contract": "契約書",
    "lecture_material": "講義資料",
    "specification": "仕様書",
    "other": "その他",
}
_INVALID_WINDOWS_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def sanitize_name_part(value: str, fallback: str) -> str:
    value = _INVALID_WINDOWS_CHARS.sub("_", value)
    value = re.sub(r"\s+", " ", value).strip().rstrip(". ")
    return value[:100] or fallback


def suggest_base_name(decision: FinalDecision) -> str:
    date_value = decision.document_date or "日付不明"
    organization = sanitize_name_part(decision.organization or "", "発行元不明")
    document_label = DOCUMENT_TYPE_LABELS[decision.document_type]
    return sanitize_name_part(f"{date_value}_{organization}_{document_label}", "解析結果")
