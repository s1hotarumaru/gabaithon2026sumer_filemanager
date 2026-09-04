from __future__ import annotations

import re
from datetime import date
from pathlib import Path

from ..models import ClassificationCandidate, DocumentType


class RuleBasedClassifier:
    KEYWORDS: dict[DocumentType, tuple[str, ...]] = {
        "receipt": ("領収書", "領収証", "合計", "税込", "お預り", "現金"),
        "invoice": ("請求書", "請求金額", "支払期限", "お支払期限", "振込先"),
        "meeting_minutes": ("議事録", "出席者", "議題", "決定事項"),
        "contract": ("契約書", "甲", "乙", "契約期間", "署名"),
        "lecture_material": ("講義資料", "レジュメ", "シラバス", "講義", "履修"),
        "specification": ("仕様書", "要件定義", "設計書"),
        "other": (),
    }
    TYPE_PRIORITY: tuple[DocumentType, ...] = (
        "receipt",
        "invoice",
        "meeting_minutes",
        "contract",
        "specification",
        "lecture_material",
    )
    DATE_PATTERNS = (
        re.compile(r"(?<!\d)(20\d{2})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日"),
        re.compile(r"(?<!\d)(20\d{2})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)"),
    )
    ORGANIZATION_PATTERNS = (
        re.compile(r"(?:発行元|発行者|請求元|販売元|会社名)\s*[:：]\s*([^\n]{1,100})"),
        re.compile(r"([^\n]{1,80}?(?:株式会社|有限会社|合同会社|Inc\.|LLC))(?=\s|$|[、,。])", re.IGNORECASE),
        re.compile(r"((?:株式会社|有限会社|合同会社)[^\n]{1,80})"),
    )

    # ファイル名中のキーワード一致は、作成者が意図して付けた強いシグナルとして本文中の
    # 偶発的な一致より重く扱う（本文が複数種別のキーワードに言及する文書――例えば本アプリ自身の
    # 仕様書のように「領収書」「請求書」「講義資料」等を分類例として列挙する文書――では、
    # 本文一致だけでは種別が割れて誤判定しやすいため）。
    FILENAME_KEYWORD_WEIGHT = 5

    def classify(self, text: str, original_file_name: str = "") -> ClassificationCandidate:
        filename_stem = Path(original_file_name).stem
        searchable = f"{text}\n{filename_stem}"
        scores: dict[DocumentType, int] = {
            document_type: (
                sum(text.count(keyword) for keyword in keywords)
                + sum(filename_stem.count(keyword) for keyword in keywords) * self.FILENAME_KEYWORD_WEIGHT
            )
            for document_type, keywords in self.KEYWORDS.items()
        }
        best_type: DocumentType = "other"
        best_score = 0
        for document_type in self.TYPE_PRIORITY:
            if scores[document_type] > best_score:
                best_type = document_type
                best_score = scores[document_type]

        matched = [keyword for keyword in self.KEYWORDS[best_type] if keyword in searchable]
        reason = "一致キーワード: " + "、".join(matched[:5]) if matched else "文書種別キーワードなし"
        return ClassificationCandidate(
            document_type=best_type,
            organization=self._extract_organization(text),
            document_date=self._extract_date(text),
            confidence=None,
            reason=reason,
            source="rules",
        )

    @classmethod
    def _extract_date(cls, text: str) -> str | None:
        for pattern in cls.DATE_PATTERNS:
            for match in pattern.finditer(text):
                try:
                    return date(*(int(value) for value in match.groups())).isoformat()
                except ValueError:
                    continue
        return None

    @classmethod
    def _extract_organization(cls, text: str) -> str | None:
        for pattern in cls.ORGANIZATION_PATTERNS:
            match = pattern.search(text)
            if not match:
                continue
            value = re.sub(r"\s+", " ", match.group(1)).strip(" 、,。:：")
            if value:
                return value[:100]
        return None

