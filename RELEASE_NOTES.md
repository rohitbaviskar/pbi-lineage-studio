# PBI Lineage Studio release notes

## v0.2.5

### Lineage path highlighting

- Hover over an asset to preview its complete upstream and downstream lineage while unrelated paths are dimmed.
- Ctrl+click an asset to pin its lineage highlight without changing the current filtered view.
- Ctrl+click additional highlighted assets to progressively narrow the view to the lineage common to those assets.
- Normal selection and Ctrl+clicking outside the highlighted path clear the pinned highlight.

### More accurate report-page usage

- Default sort metadata retained by Power BI no longer creates false measure-to-page usage links.
- Genuine visual projections, filters, formatting dependencies, and explicit user sorting remain available to lineage analysis.


## v0.2.4.1.4

### Initial release

PBI Lineage Studio is a standalone Windows application for exploring local Power BI PBIP and TMDL projects. It reads project metadata locally and turns semantic-model objects and report usage into interactive lineage views without uploading model files.

### What it does

- Loads Power BI `.SemanticModel`, `definition`, and `definition/tables` folders.
- Discovers matching sibling `.Report` projects and maps report pages to their measures.
- Visualizes sources, source schemas, source tables, model tables, columns, measures, relationships, partitions, and report pages.
- Traces upstream inputs and downstream impact across DAX measure dependencies.
- Provides interactive Data Flow and Data Model views with search, filtering, details, zooming, and custom model layouts.
- Exports the complete Data Flow canvas to PNG.
