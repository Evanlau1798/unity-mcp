"""Read-only access to the existing native test discovery resource."""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="testing",
    unity_target=None,
    description="Lists Unity tests without executing them. Supports mode/name filtering and "
                "pagination beyond the first 50 tests. Follow data.nextCursor until data.hasMore is false; "
                "native page-size limits and cursor normalization apply.",
    annotations=ToolAnnotations(
        title="List Tests", readOnlyHint=True, destructiveHint=False,
        idempotentHint=True, openWorldHint=False,
    ),
)
async def list_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"] | None, "Omit to list both modes"] = None,
    filter: Annotated[str | None, "Case-insensitive substring of test name or full name"] = None,
    page_size: Annotated[int, "Tests per page (default 50, native maximum 200)"] = 50,
    cursor: Annotated[int | str, "0-based offset; use data.nextCursor from the previous page"] = 0,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    params: dict[str, Any] = {"page_size": page_size, "cursor": cursor}
    if mode is not None:
        params["mode"] = mode
    if filter is not None:
        params["filter"] = filter
    return await send_with_unity_instance(
        async_send_command_with_retry, unity_instance, "get_tests", params,
    )
