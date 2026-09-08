# Documentation workflow validation — 2026-09-08

Native evidence: Revit 2024.3, build 24.3.30.11, disposable copies of Autodesk's
Golden Nugget MEP and Snowdon Towers Architectural samples. The existing user's
open project was not modified or restarted.

Validated Revit 2024 add-in SHA-256:
`e77a4e564b34b5a8d241a14e5bb7dbb82ac19e80583673f70cc692b48a87ea66`.
The final add-in hash is `096179a607c1c8c6988d486d658f4df6dbe5624d649d6def922fcaa02c9c5983`; its optional generated-name path, broker-version report and saved dimension/sheet readback also passed. The final native change only makes the duplicate view name optional. It uses an existing, locally trusted development signing certificate. This is not
public-trust production signing.

| Check | Result |
| --- | --- |
| Floor-plan creation on explicit level and type | Passed; level ID readback matched. |
| Activate view by exact name | Passed; active-view ID readback matched. |
| Duplicate with detailing, optional new sheet placement | Passed. |
| Full sample-sheet duplication | Passed; six viewport placements and native schedule placement checked by the API handler; writable sheet parameters preserved. Source: `revit.get_view_details` and guarded `duplicate_sheet` apply. |
| View setting preservation | Passed native comparison of template, scale, crop, scope box, phase/filter, discipline and view range. |
| Aligned annotation copy | Passed for text, a room tag, detail lines and annotation symbols. |
| Dimension copy and text edit | Passed, including reference readback and the edited above-text value. |
| Linear dimension creation and reference replacement | Passed; replacement returned a new ID, with guarded preview/apply and atomic deletion of the original dimension. |
| Annotation Undo / Redo | Passed: explicit copied-ID queries verified removal and restoration of the complete copy operation. |
| Native builds | Revit 2024/net48 and Revit 2027/net10.0-windows, zero warnings/errors. |
| Automated checks | 75 broker tests, 8 Python integration tests, TypeScript build/typecheck and repository validation passed. Windows release-package and release-evidence contracts passed. |

The live checks found and fixed unavailable dimension text-position getters,
grid-reference round-tripping, preview rollback generation tracking, and a generic
room location query that tried to read an unsupported rotation property. Committed
transactions are no longer excluded from generation tracking merely because a
caller uses the reserved preview-name prefix.

Only the 2024 host was exercised. Revit 2027 has build evidence, and Revit 2021
remains preview support without local host validation. Whole-sheet duplication is
explicitly unavailable on 2021. The architectural sample's missing external links
were left unresolved; the room tags, symbols and detail lines tested were native
to the copied document.

These are representative architectural workflows, not a claim that every Revit
annotation family, reference type, view template, or linked-model condition was
exhaustively tested. [API boundaries and usage](view-sheet-workflows.md) describe
the supported operations and failure behavior. No overall performance improvement
is claimed without a comparable benchmark.
