---
title: list_tests
sidebar_label: list_tests
description: "Lists Unity tests without executing them."
---

# `list_tests`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.list_tests`

## Description

Lists Unity tests without executing them. Supports mode/name filtering and pagination beyond the first 50 tests. Follow data.nextCursor until data.hasMore is false; native page-size limits and cursor normalization apply.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['EditMode', 'PlayMode'] \| None` | — | Omit to list both modes |
| `filter` | `str \| None` | — | Case-insensitive substring of test name or full name |
| `page_size` | `int` | — | Tests per page (default 50, native maximum 200) |
| `cursor` | `int \| str` | — | 0-based offset; use data.nextCursor from the previous page |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
List tests without starting a run:

```json
{"mode": "EditMode", "filter": "Player", "page_size": 50, "cursor": 0}
```

Keep the same target, mode and filter, then pass `data.nextCursor` as `cursor` until
`data.hasMore` is false. `data.totalCount` is the count after filtering;
`data.items` preserves native discovery order. A last or empty page has
`nextCursor: null`. The native handler caps page size at 200 and uses 50 for
nonpositive sizes; malformed cursors normalize to 0, negative cursors clamp to 0,
and cursors beyond the end clamp to `totalCount`. Native failures are returned unchanged.

The existing `mcpforunity://tests` and `mcpforunity://tests/{mode}` resources still
return their first page. Use this tool for subsequent pages, and `run_tests` only
when execution is intended. Activate the `testing` group with `manage_tools` if needed.
<!-- examples:end -->

