"""Unity transport failure contracts."""

class PluginDisconnectedError(RuntimeError):
    """Raised when a plugin WebSocket disconnects while commands are in flight."""


class NoUnitySessionError(RuntimeError):
    """Raised when no Unity plugins are available."""


class CommandDeadlineError(TimeoutError):
    def __init__(self, command: str, diagnostics: dict):
        self.diagnostics = diagnostics
        super().__init__(f"Unity '{command}' deadline exceeded during {diagnostics['phase']}; execution outcome unknown")


class InstanceSelectionRequiredError(RuntimeError):
    """Raised when the caller must explicitly select a Unity instance."""

    _SELECTION_REQUIRED = (
        "Unity instance selection is required. "
        "Call set_active_instance with Name@hash from mcpforunity://instances."
    )
    _MULTIPLE_INSTANCES = (
        "Multiple Unity instances are connected. "
        "Call set_active_instance with Name@hash from mcpforunity://instances."
    )

    def __init__(self, message: str | None = None,
                 available_instances: list[str] | None = None):
        # Carried structurally so the transport layer can surface the ids without
        # parsing the message; also appended to the text for parity with the stdio
        # guard, which lists the ids inline.
        self.available_instances = available_instances or []
        text = message or self._SELECTION_REQUIRED
        if self.available_instances:
            text = f"{text} Available instances: {self.available_instances}."
        super().__init__(text)
