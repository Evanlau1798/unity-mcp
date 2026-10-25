"""An explicit HTTP target, carried on each request rather than client identity."""
from fastmcp.server.dependencies import get_http_request
from starlette.routing import Route


def attach_instance_route(app) -> None:
    path = getattr(getattr(app, "state", None), "path", None)
    if not path:
        return
    route = next(r for r in app.router.routes if isinstance(r, Route) and r.path == path)
    app.router.routes.append(Route(
        f"{path.rstrip('/')}/instance/{{bound_unity_instance}}",
        endpoint=route.endpoint, methods=route.methods, name="unity_instance_mcp",
    ))


def get_bound_instance() -> str | None:
    try:
        request = get_http_request()
    except RuntimeError:
        return None
    value = request.path_params.get("bound_unity_instance")
    if value is not None and ("@" not in value or not all(value.rsplit("@", 1))):
        raise ValueError("Instance-bound endpoints require an exact Name@hash from mcpforunity://instances.")
    return value
