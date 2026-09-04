from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any, Literal

DocumentType = Literal[
    "receipt",
    "invoice",
    "meeting_minutes",
    "contract",
    "lecture_material",
    "specification",
    "other",
]
DOCUMENT_TYPES: tuple[DocumentType, ...] = (
    "receipt",
    "invoice",
    "meeting_minutes",
    "contract",
    "lecture_material",
    "specification",
    "other",
)


@dataclass(slots=True)
class WarningItem:
    code: str
    message: str

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(slots=True)
class ExtractionResult:
    text: str
    source: str
    confidence: float | None
    page_count: int | None
    elapsed_ms: int
    warnings: list[WarningItem] = field(default_factory=list)

    def public_dict(self, preview_chars: int = 1_000) -> dict[str, Any]:
        return {
            "source": self.source,
            "text_preview": self.text[:preview_chars],
            "character_count": len(self.text),
            "confidence": self.confidence,
            "page_count": self.page_count,
            "elapsed_ms": self.elapsed_ms,
        }


@dataclass(slots=True)
class ClassificationCandidate:
    document_type: DocumentType
    organization: str | None = None
    document_date: str | None = None
    confidence: float | None = None
    reason: str | None = None
    source: str = "rules"

    def public_dict(self, *, include_details: bool = False) -> dict[str, Any]:
        value: dict[str, Any] = {
            "document_type": self.document_type,
            "organization": self.organization,
            "document_date": self.document_date,
        }
        if include_details:
            value.update(confidence=self.confidence, reason=self.reason)
        return value


@dataclass(slots=True)
class FinalDecision:
    decision_source: Literal["slm", "rules"]
    document_type: DocumentType
    organization: str | None
    document_date: str | None
    destination_key: DocumentType
    suggested_base_name: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

