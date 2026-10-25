"""Custom tool polling response interpretation and normalization."""
from models.models import MCPResponse

_DEFAULT_POLL_INTERVAL = 1.0


class CustomToolResponseMixin:
    def _interpret_status(self, response) -> tuple[str, float]:
        if response is None:
            return "pending", _DEFAULT_POLL_INTERVAL

        if not isinstance(response, dict):
            return "final", _DEFAULT_POLL_INTERVAL

        status = response.get("_mcp_status")
        if status is None:
            if len(response.keys()) == 0:
                return "pending", _DEFAULT_POLL_INTERVAL
            return "final", _DEFAULT_POLL_INTERVAL

        if status == "pending":
            interval_raw = response.get(
                "_mcp_poll_interval", _DEFAULT_POLL_INTERVAL)
            try:
                interval = float(interval_raw)
            except (TypeError, ValueError):
                interval = _DEFAULT_POLL_INTERVAL

            interval = max(0.1, min(interval, 5.0))
            return "pending", interval

        if status == "complete":
            return "complete", _DEFAULT_POLL_INTERVAL

        if status == "error":
            return "error", _DEFAULT_POLL_INTERVAL

        return "final", _DEFAULT_POLL_INTERVAL

    def _normalize_response(self, response) -> MCPResponse:
        if isinstance(response, MCPResponse):
            return response
        if isinstance(response, dict):
            return MCPResponse(
                success=response.get("success", True),
                message=response.get("message"),
                error=response.get("error"),
                hint=response.get("hint"),
                data=response.get(
                    "data", response) if "data" not in response else response["data"],
            )

        success = True
        message = None
        error = None
        data = None

        if isinstance(response, dict):
            success = response.get("success", True)
            if "_mcp_status" in response and response["_mcp_status"] == "error":
                success = False
            message = str(response.get("message")) if response.get(
                "message") else None
            error = str(response.get("error")) if response.get(
                "error") else None
            data = response.get("data")
            if "success" not in response and "_mcp_status" not in response:
                data = response
        else:
            success = False
            message = str(response)

        return MCPResponse(success=success, message=message, error=error, data=data)

    def _safe_response(self, response):
        if isinstance(response, dict):
            return response
        if response is None:
            return None
        return {"message": str(response)}
